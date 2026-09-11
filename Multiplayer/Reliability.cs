using System;
using BepInEx.Logging;
using Stitch;
using UnityEngine;

// The reliability layer itself now lives in Stitch (lib/Stitch.dll, https://github.com/Zonda001/stitch) —
// same algorithm, same wire format, extracted so it can be tested without a game attached and reused
// outside this mod. What is left here is the glue: the static facade the rest of the mod already calls,
// plus the three things the library deliberately does not know about.
//
//   Unity time      -> injected clock, so retransmit timing stays on Time.time exactly as before
//   Steam peer id   -> injected predicate, so Send() still drops while no peer is set
//   BepInEx logging -> injected sink, severity preserved (the backoff-ceiling line stays a WARNING,
//                      which is what makes it visible in an ordinary player's log)
//
// Nothing about the protocol changed, so a build with this file talks to 0.1.5.1 and earlier without
// any version bump: DATA [0xFE][epoch][seq][more], ACK [0xFF][epoch][ackThrough], same defaults.
//
// Routing is unchanged too and still lives in SteamManager.SendPacket:
//   0x06/0x07/0x08 position/anim/time -> raw unreliable (superseded next tick; loss is fine)
//   0x01..0x05, 0x22, 0x23            -> native Steam reliable (setup + host->client, proven OK)
//   everything else (gameplay)        -> this layer
public static class ReliableNet
{
    // Injected once by SteamManager at init, exactly as before.
    public static Action<byte[]>  RawSend;    // send one datagram UNRELIABLE to RemoteID
    public static Action<byte[]>  OnDeliver;  // hand a fully reassembled inner message to the dispatcher
    public static ManualLogSource Log;

    // Held separately so CycleChaos can change the drop rate at runtime: the channel reads it per datagram.
    static readonly StitchOptions _options = new StitchOptions();

    static ReliableChannel _channel;

    // Built on first use rather than at field-assignment time: RawSend/OnDeliver are assigned by
    // SteamManager after this class is first touched, and going through the static fields means the
    // order of those assignments cannot matter.
    static ReliableChannel Channel
    {
        get
        {
            if (_channel == null)
            {
                _channel = new ReliableChannel(
                    rawSend:     bytes => RawSend?.Invoke(bytes),
                    onDeliver:   msg   => OnDeliver?.Invoke(msg),
                    clock:       ()    => Time.time,
                    peerPresent: ()    => SteamNetwork.RemoteID != 0,
                    log:         WriteLog,
                    options:     _options);
            }
            return _channel;
        }
    }

    static void WriteLog(StitchLogLevel level, string message)
    {
        if (level == StitchLogLevel.Warning) Log?.LogWarning(message);
        else                                 Log?.LogInfo(message);
    }

    // ── DEBUG chaos injector: simulated packet loss ───────────────────────────────────────────────────
    // The gameplay races (chest "-1 on cursor", half-drawn grave) only surface under packet loss, because
    // this layer delivers IN ORDER: a lost fragment makes later ones pile up, and the whole contiguous run
    // then drains in ONE frame once the gap fills. That burst applies a clump of ops at once and opens the
    // race window. A clean LAN never bursts, which is why a friend on lossy dorm/CGNAT internet hit these
    // and a two-PC LAN test never does. Toggled by Shift+C. OFF in production.
    public static bool ChaosEnabled => _options.ChaosDropRate > 0.0;

    public static float ChaosDropRate
    {
        get => (float)_options.ChaosDropRate;
        set => _options.ChaosDropRate = value;
    }

    // Cycle OFF → 10% → 25% → 40% → OFF so you can walk the loss up until the bug appears.
    public static void CycleChaos()
    {
        double[] steps = { 0.0, 0.10, 0.25, 0.40 };
        int cur = 0;
        for (int i = 0; i < steps.Length; i++)
            if (Math.Abs(steps[i] - _options.ChaosDropRate) < 0.001) { cur = i; break; }

        _options.ChaosDropRate = steps[(cur + 1) % steps.Length];
        Log?.LogWarning($"[CHAOS] packet-loss injector → " +
                        $"{(ChaosEnabled ? $"{_options.ChaosDropRate * 100.0:0}% drop" : "OFF")} " +
                        $"(dropped {Channel.ChaosDropped} datagrams so far)");
    }

    // ── TX accounting (bandwidth diagnostic) ─────────────────────────────────────────────────────────
    // A retransmit storm and a legitimate bulk transfer look identical on a plain byte total, so each
    // datagram is classified where it is sent.
    public static int  TxNew        => Channel.TxNew;
    public static int  TxRetx       => Channel.TxRetx;
    public static int  TxAck        => Channel.TxAck;
    public static long TxBytes      => Channel.TxBytes;
    public static int  TxMaxTries   => Channel.TxMaxTries;
    public static int  UnackedCount => Channel.UnackedCount;
    public static int  QueuedCount  => Channel.QueuedCount;

    public static void ResetTxStats() => Channel.ResetTxStats();

    // ── Outbound-death signals (read by SteamManager's degraded-link detector) ───────────────────────
    // A side whose OUTBOUND died still receives the peer just fine, so the inbound-silence detector never
    // fires HERE — only the other machine warns. These catch the local half.
    public static bool   OutboundLooksDead   => Channel.OutboundLooksDead;
    public static string OutboundDeathReason => Channel.OutboundDeathReason;

    // ── Stream lifecycle ─────────────────────────────────────────────────────────────────────────────
    public static void Reset()                          => Channel.Reset();
    public static void Reset(byte epoch)                => Channel.Reset(epoch);
    public static void Stop()                           => Channel.Stop();
    public static void PrepareResync(byte pendingEpoch) => Channel.PrepareResync(pendingEpoch);
    public static void Resume()                         => Channel.Resume();

    // ── Traffic ──────────────────────────────────────────────────────────────────────────────────────
    public static void Send(byte[] payload)       => Channel.Send(payload);
    public static void HandleIncoming(byte[] data) => Channel.HandleIncoming(data);

    // dt is ignored: the channel reads the injected clock directly. The parameter is kept so the call
    // site in Update() — ReliableNet.Tick(Time.deltaTime) — does not have to change.
    public static void Tick(float dt) => Channel.Tick();
}
