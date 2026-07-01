using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

// C3 reliability layer over Steam UNRELIABLE P2P (SendP2PPacket / k_EP2PSendUnreliable).
//
// WHY: on this game's Steamworks (SDK 1.42 / Steamworks.NET 11.0.0) Steam's own k_EP2PSendReliable silently
// drops every packet client→host mid-session — PROVEN by the 0x24 heartbeat diag: client sent 43, host got 0,
// while unreliable flowed 30-48/2s BOTH ways with zero P2PSessionConnectFail. The modern relay-backed
// SteamNetworkingMessages/Sockets API is absent (added in SDK 1.44+). So we build TCP-lite over the one
// primitive that demonstrably works: unreliable datagrams. In-order, ACKed, retransmitted, fragmented.
//
// WIRE (every frame all sent UNRELIABLE via RawSend):
//   DATA: [0xFE][seq:uint32][more:1][payload bytes...]   more=1 → another fragment of the same message follows
//   ACK : [0xFF][ackThrough:uint32]                      cumulative — receiver has delivered every seq ≤ ackThrough
// 0xFE/0xFF are outside the app protocol range (0x01..0x24).
//
// Bypass this layer (see SteamManager.SendPacket routing):
//   - 0x06/0x07/0x08 position/anim/time → raw unreliable (superseded next tick; loss is fine)
//   - 0x01..0x05, 0x22, 0x23 handshake/save/version → native Steam reliable (setup + host→client, proven OK)
// Everything else (gameplay) rides this layer. Reset() on each session (re)start aligns both seq spaces to 0.
public static class ReliableNet
{
    const byte  TAG_DATA     = 0xFE;
    const byte  TAG_ACK      = 0xFF;
    const int   FRAG_PAYLOAD = 1100;   // inner bytes per datagram (+6 header < Steam ~1200 unreliable cap)
    const float RESEND_AFTER = 0.30f;  // retransmit an unacked fragment after this many seconds
    const float ACK_COALESCE = 0.03f;  // batch the cumulative ack instead of one per fragment
    const int   MAX_REORDER  = 8192;   // safety cap on the out-of-order buffer

    // Injected once by SteamManager at init
    public static Action<byte[]>     RawSend;    // send one datagram UNRELIABLE to RemoteID
    public static Action<byte[]>     OnDeliver;  // hand a fully reassembled inner message to the dispatcher
    public static ManualLogSource    Log;

    // ── DEBUG chaos injector: simulated packet loss ───────────────────────────────────────────────────────────
    // The gameplay races (chest "-1 on cursor", half-drawn grave) only surface under packet loss. Reason: this
    // layer delivers IN ORDER — when a fragment is lost, later ones pile up in _reorder and the whole contiguous
    // run drains in ONE frame the moment the gap fills (the while-loop in HandleIncoming). That burst applies a
    // clump of ops at once → opens the race window. A clean LAN never bursts, so those bugs can't be reproduced
    // on it — which is why a friend on lossy dorm/CGNAT internet hit them and a two-PC LAN test never does.
    // This drops a configurable fraction of OUTGOING datagrams — DATA, retransmits AND acks, exactly what a lossy
    // uplink does — to force the bursts on demand. Toggled by a debug key (Shift+C). OFF in production.
    public static bool  ChaosEnabled;
    public static float ChaosDropRate;                                 // 0..1 — P(an outgoing datagram is dropped)
    static readonly System.Random _chaosRng = new System.Random();
    static int _chaosDropped, _chaosSent;

    // Cycle OFF → 10% → 25% → 40% → OFF so you can walk the loss up until the bug appears.
    public static void CycleChaos()
    {
        float[] steps = { 0f, 0.10f, 0.25f, 0.40f };
        int cur = 0;
        for (int i = 0; i < steps.Length; i++)
            if (Mathf.Approximately(steps[i], ChaosDropRate)) { cur = i; break; }
        ChaosDropRate = steps[(cur + 1) % steps.Length];
        ChaosEnabled  = ChaosDropRate > 0f;
        Log?.LogWarning($"[CHAOS] packet-loss injector → {(ChaosEnabled ? $"{ChaosDropRate * 100f:0}% drop" : "OFF")} " +
                        $"(dropped {_chaosDropped}/{_chaosSent} datagrams so far)");
    }

    // Every outgoing datagram funnels through here. When chaos is on, silently swallow a fraction of them so the
    // reliability layer must retransmit — reproducing the real bursty, delayed delivery of a lossy link.
    static void WireSend(byte[] wire)
    {
        if (ChaosEnabled)
        {
            _chaosSent++;
            if (_chaosRng.NextDouble() < ChaosDropRate) { _chaosDropped++; return; }   // "lost" — never hits the wire
        }
        RawSend?.Invoke(wire);
    }

    // ── Send side: our outgoing reliable stream ──
    static uint _sendSeq;
    class Pending { public byte[] wire; public float lastSent; public int tries; }
    static readonly SortedDictionary<uint, Pending> _unacked = new SortedDictionary<uint, Pending>();

    // ── Recv side: the partner's incoming reliable stream ──
    static uint _recvNext;                                                         // next in-order seq to deliver
    static readonly Dictionary<uint, byte[]> _reorder = new Dictionary<uint, byte[]>();  // buffered future fragments
    static readonly List<byte> _assembly = new List<byte>();                       // accumulates the current message
    static bool  _ackDue;
    static float _ackTimer;
    static bool  _ready;   // only true after a handshake Reset — blocks premature seq-0 sends before both sides align

    public static void Reset()
    {
        _sendSeq = 0; _recvNext = 0;
        _unacked.Clear(); _reorder.Clear(); _assembly.Clear();
        _ackDue = false; _ackTimer = 0f;
        _ready = true;
        Log?.LogInfo("[REL] reset — reliable streams realigned to 0 (session (re)start)");
    }

    // Peer gone: stop resending into the void and block sends until the next handshake Reset().
    public static void Stop()
    {
        _unacked.Clear(); _reorder.Clear(); _assembly.Clear();
        _ackDue = false; _ackTimer = 0f;
        _ready = false;
    }

    // Queue a logical message: fragment it, store each fragment for retransmit, fire all now.
    public static void Send(byte[] payload)
    {
        if (payload == null || payload.Length == 0) return;
        if (!_ready || SteamNetwork.RemoteID == 0) return;   // not past the handshake yet, or no peer — drop
        //  ↑ blocks any layer traffic (heartbeat/weather/time) the host might emit after setting RemoteID but
        //    BEFORE receiving 0x01 — sending seq 0 then would desync the stream when Reset() rewinds it.
        float now = Time.time;
        int off = 0;
        do
        {
            int chunk = Math.Min(FRAG_PAYLOAD, payload.Length - off);
            bool more = (off + chunk) < payload.Length;
            uint seq  = _sendSeq++;
            var wire  = new byte[6 + chunk];
            wire[0] = TAG_DATA;
            BitConverter.GetBytes(seq).CopyTo(wire, 1);
            wire[5] = (byte)(more ? 1 : 0);
            Buffer.BlockCopy(payload, off, wire, 6, chunk);
            _unacked[seq] = new Pending { wire = wire, lastSent = now, tries = 1 };
            WireSend(wire);
            off += chunk;
        } while (off < payload.Length);
    }

    // A 0xFE/0xFF datagram arrived. Returns nothing — delivers reassembled messages via OnDeliver, acks via RawSend.
    public static void HandleIncoming(byte[] data)
    {
        if (data == null || data.Length < 1) return;

        if (data[0] == TAG_ACK)
        {
            if (data.Length < 5) return;
            uint ackThrough = BitConverter.ToUInt32(data, 1);
            // Drop every fragment the peer confirmed. _unacked is sorted, so we can stop at the first unconfirmed.
            var done = new List<uint>();
            foreach (var kv in _unacked) { if (kv.Key <= ackThrough) done.Add(kv.Key); else break; }
            for (int i = 0; i < done.Count; i++) _unacked.Remove(done[i]);
            return;
        }

        if (data[0] == TAG_DATA)
        {
            if (data.Length < 6) return;
            uint seq = BitConverter.ToUInt32(data, 1);

            if (seq < _recvNext)            { _ackDue = true; return; }   // duplicate — re-ack so the sender drops it
            if (seq == _recvNext)
            {
                DeliverFragment(data);
                _recvNext++;
                while (_reorder.TryGetValue(_recvNext, out var buf))      // drain the now-contiguous run
                {
                    _reorder.Remove(_recvNext);
                    DeliverFragment(buf);
                    _recvNext++;
                }
            }
            else if (_reorder.Count < MAX_REORDER && !_reorder.ContainsKey(seq))
            {
                _reorder[seq] = data;                                     // future fragment — buffer until the gap fills
            }
            _ackDue = true;
        }
    }

    // Strip the 6-byte header, append to the assembly buffer; on more=0 emit the complete inner message.
    static void DeliverFragment(byte[] wire)
    {
        int len = wire.Length - 6;
        for (int i = 0; i < len; i++) _assembly.Add(wire[6 + i]);
        if (wire[5] == 0)   // last fragment of this message
        {
            var msg = _assembly.ToArray();
            _assembly.Clear();
            if (msg.Length > 0) OnDeliver?.Invoke(msg);
        }
    }

    // Called every Update: retransmit timed-out fragments + flush a coalesced cumulative ack.
    public static void Tick(float dt)
    {
        float now = Time.time;
        foreach (var kv in _unacked)
        {
            var p = kv.Value;
            if (now - p.lastSent >= RESEND_AFTER)
            {
                WireSend(p.wire);
                p.lastSent = now;
                p.tries++;
            }
        }

        if (_ackDue && _recvNext > 0)   // _recvNext==0 → nothing delivered yet, an ack would be meaningless
        {
            _ackTimer += dt;
            if (_ackTimer >= ACK_COALESCE)
            {
                _ackTimer = 0f; _ackDue = false;
                var ack = new byte[5];
                ack[0] = TAG_ACK;
                BitConverter.GetBytes(_recvNext - 1).CopyTo(ack, 1);
                WireSend(ack);
            }
        }
    }
}
