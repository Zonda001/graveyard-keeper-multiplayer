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
//   DATA: [0xFE][epoch:1][seq:uint32][more:1][payload...]  more=1 → another fragment of the same message follows
//   ACK : [0xFF][epoch:1][ackThrough:uint32]               cumulative — receiver delivered every seq ≤ ackThrough
// 0xFE/0xFF are outside the app protocol range (0x01..0x26).
// epoch = stream generation, bumped by the 0x25/0x26 auto-resync (see SteamManager). Frames from another
// epoch are dropped on arrival: without this, stale in-flight fragments from before a Reset could land in
// _reorder and later be delivered INSTEAD of the true fragment with the same seq (silent stream corruption).
//
// Bypass this layer (see SteamManager.SendPacket routing):
//   - 0x06/0x07/0x08 position/anim/time → raw unreliable (superseded next tick; loss is fine)
//   - 0x01..0x05, 0x22, 0x23 handshake/save/version → native Steam reliable (setup + host→client, proven OK)
// Everything else (gameplay) rides this layer. Reset() on each session (re)start aligns both seq spaces to 0.
public static class ReliableNet
{
    const byte  TAG_DATA     = 0xFE;
    const byte  TAG_ACK      = 0xFF;
    const int   FRAG_PAYLOAD = 1100;   // inner bytes per datagram (+7 header < Steam ~1200 unreliable cap)
    const float RESEND_AFTER = 0.30f;  // retransmit an unacked fragment after this many seconds
    const float ACK_COALESCE = 0.03f;  // batch the cumulative ack instead of one per fragment
    const int   MAX_REORDER  = 8192;   // safety cap on the out-of-order buffer
    const int   SEND_WINDOW  = 96;     // max fragments in flight (~106KB). A multi-MB payload burst-sent all
                                       // at once overflows Steam's per-connection send buffer: SendP2PPacket
                                       // starts returning false for EVERYTHING including other systems' packets,
                                       // and our own ACKs never come back — the detector then reads the
                                       // self-inflicted silence as a dead link and a resync wipes the payload.
                                       // Fragments beyond the window wait in _sendQueue and follow as ACKs
                                       // free slots. (Proven in the BFTG port: 3MB save / ~2800 fragments.)

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

    // ── TX accounting (bandwidth diagnostic, wild report 2026-08-10) ──────────────────────
    // A tester's whole connection degraded while the mod ran — Discord voice broke up — and it happened
    // even while he sat in the MENU, which neither the save transfer (one-shot) nor 0x06 (IsInGame only)
    // can explain. Retransmits are the suspect: Tick() runs always and re-sends every unacked fragment
    // every RESEND_AFTER with no backoff, so a peer that stops acking makes us hammer the link forever.
    // A plain byte total can't show that — a retransmit storm and a legitimate bulk transfer look
    // identical on the scale. So each datagram is classified where it is sent.
    public static int  TxNew      { get; private set; }   // fragments put on the wire for the first time
    public static int  TxRetx     { get; private set; }   // re-sends of a fragment already sent
    public static int  TxAck      { get; private set; }   // cumulative-ack datagrams
    public static long TxBytes    { get; private set; }   // total bytes handed to Steam (all three kinds)
    public static int  TxMaxTries { get; private set; }   // worst per-fragment try count seen this window
    public static int  UnackedCount => _unacked.Count;
    public static int  QueuedCount  => _sendQueue.Count;

    public static void ResetTxStats() { TxNew = TxRetx = TxAck = 0; TxBytes = 0; TxMaxTries = 0; }

    const int TX_NEW = 0, TX_RETX = 1, TX_ACK = 2;

    // Every outgoing datagram funnels through here. When chaos is on, silently swallow a fraction of them so the
    // reliability layer must retransmit — reproducing the real bursty, delayed delivery of a lossy link.
    static void WireSend(byte[] wire, int kind)
    {
        if (ChaosEnabled)
        {
            _chaosSent++;
            if (_chaosRng.NextDouble() < ChaosDropRate) { _chaosDropped++; return; }   // "lost" — never hits the wire
        }
        // Counted after the chaos gate: this measures what actually reaches the uplink, which is the
        // number the bandwidth question is about.
        if      (kind == TX_RETX) TxRetx++;
        else if (kind == TX_ACK)  TxAck++;
        else                      TxNew++;
        TxBytes += wire.Length;
        RawSend?.Invoke(wire);
    }

    // ── Send side: our outgoing reliable stream ──
    static uint _sendSeq;
    class Pending { public uint seq; public byte[] wire; public float firstSent; public float lastSent; public int tries; }
    static readonly SortedDictionary<uint, Pending> _unacked = new SortedDictionary<uint, Pending>();
    static readonly Queue<Pending> _sendQueue = new Queue<Pending>();   // built but not yet on the wire

    // ── Outbound-death signals (read by SteamManager's degraded-link detector) ──
    // A side whose OUTBOUND died (post-Stop zombie, or the peer stopped acking) still receives the peer
    // just fine, so the inbound-silence detector never fires HERE — proven by the 2026-07-03 Shift+X live
    // test: only the OTHER machine warned. These two signals catch the local half of the breakage.
    static float _notReadyDropSince;                       // first Send() swallowed by !_ready since Reset (0 = none)
    static float _lastOutboundProgressAt;                  // last time the peer's cumulative ack advanced our window
    public static bool   OutboundLooksDead  { get; private set; }
    public static string OutboundDeathReason { get; private set; }

    // ── Recv side: the partner's incoming reliable stream ──
    static uint _recvNext;                                                         // next in-order seq to deliver
    static readonly Dictionary<uint, byte[]> _reorder = new Dictionary<uint, byte[]>();  // buffered future fragments
    static readonly List<byte> _assembly = new List<byte>();                       // accumulates the current message
    static bool  _ackDue;
    static float _ackTimer;
    static bool  _ready;   // only true after a handshake Reset — blocks premature seq-0 sends before both sides align
    static byte  _epoch;   // current stream generation (see the wire spec above)

    public static void Reset() { Reset(0); }   // session (re)start via the 0x01 handshake → generation 0

    public static void Reset(byte epoch)
    {
        _sendSeq = 0; _recvNext = 0;
        _unacked.Clear(); _sendQueue.Clear(); _reorder.Clear(); _assembly.Clear();
        _ackDue = false; _ackTimer = 0f;
        _ready = true;
        _epoch = epoch;
        _notReadyDropSince = 0f; OutboundLooksDead = false; OutboundDeathReason = null;
        _lastOutboundProgressAt = Time.time;
        Log?.LogInfo($"[REL] reset — reliable streams realigned to 0 (epoch {epoch})");
    }

    // Peer gone: stop resending into the void and block sends until the next handshake Reset().
    public static void Stop()
    {
        _unacked.Clear(); _sendQueue.Clear(); _reorder.Clear(); _assembly.Clear();
        _ackDue = false; _ackTimer = 0f;
        _ready = false;
    }

    // ── 0x25/0x26 auto-resync support (SteamManager drives the exchange) ──
    // Initiator half: align to the PENDING epoch and go quiet. Receiving is fully functional (HandleIncoming
    // ignores _ready), so the peer's post-Reset seq-0 stream is accepted+acked immediately; our own sends stay
    // blocked until Resume() — sending seq 0 before the peer resets would desync the fresh streams.
    public static void PrepareResync(byte pendingEpoch)
    {
        _sendSeq = 0; _recvNext = 0;
        _unacked.Clear(); _sendQueue.Clear(); _reorder.Clear(); _assembly.Clear();
        _ackDue = false; _ackTimer = 0f;
        _ready = false;
        _epoch = pendingEpoch;
        _notReadyDropSince = 0f;
        Log?.LogInfo($"[REL] resync prepared — waiting for the peer to realign (epoch {pendingEpoch})");
    }

    // Initiator half 2: the peer confirmed (0x26). ONLY unblock sending — a full Reset here would rewind
    // _recvNext past anything the peer already delivered since ITS Reset and redeliver duplicates.
    public static void Resume()
    {
        _ready = true;
        _notReadyDropSince = 0f; OutboundLooksDead = false; OutboundDeathReason = null;
        _lastOutboundProgressAt = Time.time;
    }

    // Queue a logical message: fragment it, enqueue the fragments; the send window paces them onto the wire.
    public static void Send(byte[] payload)
    {
        if (payload == null || payload.Length == 0) return;
        if (!_ready || SteamNetwork.RemoteID == 0)           // not past the handshake yet, or no peer — drop
        {
            // Swallowing sends while a peer exists = the post-Stop zombie half; timestamp the first one
            // so Tick can flag the outbound as dead if it goes on (Reset clears it).
            if (!_ready && SteamNetwork.RemoteID != 0 && _notReadyDropSince == 0f) _notReadyDropSince = Time.time;
            return;
        }
        //  ↑ blocks any layer traffic (heartbeat/weather/time) the host might emit after setting RemoteID but
        //    BEFORE receiving 0x01 — sending seq 0 then would desync the stream when Reset() rewinds it.
        int off = 0;
        do
        {
            int chunk = Math.Min(FRAG_PAYLOAD, payload.Length - off);
            bool more = (off + chunk) < payload.Length;
            uint seq  = _sendSeq++;
            var wire  = new byte[7 + chunk];
            wire[0] = TAG_DATA;
            wire[1] = _epoch;
            BitConverter.GetBytes(seq).CopyTo(wire, 2);
            wire[6] = (byte)(more ? 1 : 0);
            Buffer.BlockCopy(payload, off, wire, 7, chunk);
            _sendQueue.Enqueue(new Pending { seq = seq, wire = wire });
            off += chunk;
        } while (off < payload.Length);
        PumpSendQueue(Time.time);
    }

    // Move queued fragments onto the wire while the in-flight window has room. Queued wires
    // always carry the current epoch: Reset/PrepareResync/Stop clear the queue along with _unacked.
    static void PumpSendQueue(float now)
    {
        // An idle→busy transition re-anchors the progress clock: a stale timestamp from the
        // last burst must not count against the fresh one.
        if (_unacked.Count == 0 && _sendQueue.Count > 0) _lastOutboundProgressAt = now;
        while (_sendQueue.Count > 0 && _unacked.Count < SEND_WINDOW)
        {
            var p = _sendQueue.Dequeue();
            p.firstSent = now; p.lastSent = now; p.tries = 1;
            _unacked[p.seq] = p;
            WireSend(p.wire, TX_NEW);
        }
    }

    // A 0xFE/0xFF datagram arrived. Returns nothing — delivers reassembled messages via OnDeliver, acks via RawSend.
    public static void HandleIncoming(byte[] data)
    {
        if (data == null || data.Length < 1) return;

        if (data[0] == TAG_ACK)
        {
            if (data.Length < 6) return;
            if (data[1] != _epoch) return;   // stale generation — ignore
            uint ackThrough = BitConverter.ToUInt32(data, 2);
            // Drop every fragment the peer confirmed. _unacked is sorted, so we can stop at the first unconfirmed.
            var done = new List<uint>();
            foreach (var kv in _unacked) { if (kv.Key <= ackThrough) done.Add(kv.Key); else break; }
            for (int i = 0; i < done.Count; i++) _unacked.Remove(done[i]);
            if (done.Count > 0)
            {
                _lastOutboundProgressAt = Time.time;
                PumpSendQueue(Time.time);   // freed window slots — let queued fragments follow
            }
            return;
        }

        if (data[0] == TAG_DATA)
        {
            if (data.Length < 7) return;
            if (data[1] != _epoch) return;   // stale generation — never let it into _reorder (poison, see wire spec)
            uint seq = BitConverter.ToUInt32(data, 2);

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

    // Strip the 7-byte header, append to the assembly buffer; on more=0 emit the complete inner message.
    static void DeliverFragment(byte[] wire)
    {
        int len = wire.Length - 7;
        for (int i = 0; i < len; i++) _assembly.Add(wire[7 + i]);
        if (wire[6] == 0)   // last fragment of this message
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
        PumpSendQueue(now);   // covers the Resume() case: queue drained even with no fresh ACK or Send
        foreach (var kv in _unacked)
        {
            var p = kv.Value;
            if (now - p.lastSent >= RESEND_AFTER)
            {
                WireSend(p.wire, TX_RETX);
                p.lastSent = now;
                p.tries++;
                if (p.tries > TxMaxTries) TxMaxTries = p.tries;
            }
        }

        // Outbound health for the degraded-link detector: dead if we've been swallowing sends (post-Stop
        // zombie) or the peer's cumulative ack has not advanced our window for a long stretch. The yardstick
        // must be ACK PROGRESS, not the age of the oldest fragment: during a windowed bulk transfer a
        // fragment legitimately waits many seconds behind the queue on a slow-but-alive link, and reading
        // that age as death produced resync → transfer restart → repeat, an infinite join loop under
        // sustained packet loss (proven in the BFTG port under 35% chaos).
        float ackStall = _unacked.Count > 0 ? now - _lastOutboundProgressAt : 0f;
        if (_notReadyDropSince != 0f && now - _notReadyDropSince > 12f)
            { OutboundLooksDead = true; OutboundDeathReason = "sends blocked — layer stopped, no new handshake"; }
        else if (ackStall > 12f)
            { OutboundLooksDead = true; OutboundDeathReason = $"no ACK progress for {ackStall:F0}s"; }
        else if (OutboundLooksDead && _notReadyDropSince == 0f && ackStall < 2f)
            { OutboundLooksDead = false; OutboundDeathReason = null; }   // acks resumed — recovered

        if (_ackDue && _recvNext > 0)   // _recvNext==0 → nothing delivered yet, an ack would be meaningless
        {
            _ackTimer += dt;
            if (_ackTimer >= ACK_COALESCE)
            {
                _ackTimer = 0f; _ackDue = false;
                var ack = new byte[6];
                ack[0] = TAG_ACK;
                ack[1] = _epoch;
                BitConverter.GetBytes(_recvNext - 1).CopyTo(ack, 2);
                WireSend(ack, TX_ACK);
            }
        }
    }
}
