using System;
using System.Collections.Generic;

namespace Lumigram.Voip
{
    /// <summary>
    /// Holds arriving speech long enough to play it in order.
    ///
    /// The network delivers frames late, out of order, twice, or not at all, and the
    /// speaker needs one every frame regardless. This is the piece that turns the
    /// first into the second, and the only tool it has is delay: hold a little audio
    /// back, and a frame that arrives out of order still arrives before its turn.
    ///
    /// The whole design is one trade. Too little delay and every hiccup is a gap you
    /// can hear; too much and the two people start talking over each other because
    /// the pauses have moved. It starts short and grows when the network proves it
    /// needs to - a phone that was on wifi a minute ago may be on a train now.
    ///
    /// How long a frame lasts is measured, not assumed. libtgvoip sends 60 ms by
    /// default, other clients send 20, and either is legal; a buffer that assumes
    /// one and receives the other looks for timestamps that will never exist and
    /// plays nothing at all while reporting a perfectly healthy connection.
    /// </summary>
    public sealed class JitterBuffer
    {
        /// <summary>What to assume until frames have been seen to measure.</summary>
        public const int DefaultFrameDuration = 20;

        /// <summary>
        /// How much audio to hold back, in milliseconds rather than frames.
        ///
        /// Counting frames made the delay depend on the sender's frame size, which
        /// is the opposite of what is wanted: two frames is 40 ms of protection from
        /// a client sending 20 ms frames and 120 ms from one sending 60. What
        /// matters is the time, because that is what has to cover the network's
        /// unevenness.
        ///
        /// A parameter rather than a constant because it is a policy, and a test
        /// that has to feed nine frames to see one played is testing the policy
        /// instead of the mechanism.
        /// </summary>
        private readonly int _startingDelayMs;
        private readonly int _maxDelayMs;

        /// <summary>Frames to see before the spacing between them can be measured.</summary>
        private const int MinimumToMeasure = 2;

        public JitterBuffer() : this(180, 500) { }

        public JitterBuffer(int startingDelayMs, int maxDelayMs)
        {
            _startingDelayMs = startingDelayMs;
            _maxDelayMs = maxDelayMs;
        }

        /// <summary>
        /// Underruns before accepting that the connection needs more slack.
        ///
        /// Not the first one: a single late frame is ordinary, and lengthening the
        /// delay for it would ratchet up on any connection until the call had a
        /// noticeable lag.
        /// </summary>
        private const int PatienceBeforeGrowing = 3;

        /// <summary>
        /// How far behind the buffer may fall before giving up and skipping ahead.
        ///
        /// A sender that restarts its clock, or a stall long enough to fall hopeless
        /// behind, would otherwise leave the buffer marching through timestamps that
        /// will never arrive while frames pile up in front of it.
        /// </summary>
        private const int LostCauseFrames = 5;

        private readonly Dictionary<int, byte[]> _frames = new Dictionary<int, byte[]>();
        private readonly object _gate = new object();

        private int _next = -1;
        private int _step = DefaultFrameDuration;
        private bool _measured;
        private int _depth = MinimumToMeasure;
        private int _consecutiveMisses;
        private bool _playing;

        public int Count { get { lock (_gate) return _frames.Count; } }

        /// <summary>How long one frame lasts, as measured from what arrives.</summary>
        public int FrameDuration { get { lock (_gate) return _step; } }

        /// <summary>The delay currently being held, in milliseconds.</summary>
        public int DelayMs { get { lock (_gate) return _depth * _step; } }

        /// <summary>Frames needed to cover a given stretch of time.</summary>
        private static int DepthFor(int milliseconds, int step)
        {
            int frames = (milliseconds + step - 1) / step;
            return frames < 1 ? 1 : frames;
        }

        /// <summary>Frames that arrived too late to be played, and were dropped.</summary>
        public int Late;

        /// <summary>Frames that were never there when their turn came.</summary>
        public int Missing;

        public void Put(int timestamp, byte[] frame)
        {
            if (frame == null || frame.Length == 0) return;

            lock (_gate)
            {
                // The first frame sets the clock. Whatever the far end counts from
                // is where playback starts; the absolute value means nothing here.
                if (_next < 0) _next = timestamp;

                if (timestamp < _next)
                {
                    // Its turn has passed. Playing it now would put a syllable in
                    // the wrong place, which is worse than the gap already heard.
                    Late++;
                    return;
                }

                // A duplicate or a retransmission. The first copy is as good.
                if (_frames.ContainsKey(timestamp)) return;

                _frames[timestamp] = frame;

                // Guard against a sender whose timestamps run away, or a long stall
                // in playback: without this the buffer would grow without limit.
                if (_frames.Count > DepthFor(_maxDelayMs, _step) * 4) DropOldest();
            }
        }

        /// <summary>
        /// The next frame to play, or null when there is nothing to play yet.
        ///
        /// Null means two things and the caller need not care: either playback has
        /// not started because the buffer is still filling, or a frame is missing
        /// and the decoder should conceal the gap.
        /// </summary>
        public byte[] Get()
        {
            lock (_gate)
            {
                if (_next < 0) return null;

                if (!_playing)
                {
                    if (_frames.Count < _depth) return null;
                    if (_frames.Count < MinimumToMeasure) return null;

                    // Enough frames are in hand to see the spacing between them,
                    // which is the only reliable way to learn how long one lasts.
                    Measure();

                    // And once it is known, how many of them make up the delay this
                    // is meant to be holding.
                    if (_measured)
                    {
                        int wanted = DepthFor(_startingDelayMs, _step);
                        if (wanted > _depth) _depth = wanted;

                        if (_frames.Count < _depth) return null;
                    }

                    _playing = true;
                }

                byte[] frame;

                if (_frames.TryGetValue(_next, out frame))
                {
                    _frames.Remove(_next);
                    _next += _step;
                    _consecutiveMisses = 0;
                    return frame;
                }

                Missing++;

                // Nothing at all, as opposed to a hole with frames behind it. These
                // need opposite treatment and conflating them is fatal:
                //
                //   a hole      frames exist past this slot, so the one that belongs
                //               here is lost. Move on, or everything after it is
                //               late too.
                //
                //   starvation  nothing has arrived yet. Moving on runs the clock
                //               ahead of the sender, and every frame that then
                //               arrives is behind it, counted late and thrown away -
                //               so a single stall silences the call permanently
                //               while the packets keep coming.
                //
                // The clock only advances when there is evidence to advance past.
                if (_frames.Count == 0)
                {
                    // Starved. Hold everything - the clock, the depth, and the fact
                    // that playback has started - and wait. Deepening the buffer
                    // here would be treating the player's appetite as network
                    // trouble, and the refill it forces is itself another silence.
                    return null;
                }

                _next += _step;
                Realign();

                // Holes are the network's doing, and repeated holes are what more
                // delay actually helps with.
                if (++_consecutiveMisses >= PatienceBeforeGrowing &&
                    _depth < DepthFor(_maxDelayMs, _step))
                {
                    _depth++;
                    _consecutiveMisses = 0;

                    // Fill again before playing on, or the extra depth is nominal
                    // and the buffer keeps running at the level that was failing.
                    _playing = false;
                }

                return null;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _frames.Clear();
                _next = -1;
                _step = DefaultFrameDuration;
                _measured = false;
                _depth = MinimumToMeasure;
                _consecutiveMisses = 0;
                _playing = false;
            }
        }

        /// <summary>
        /// Learns the frame duration from the gaps between what is waiting.
        ///
        /// The smallest positive difference, because a gap of two frames is two
        /// steps and would otherwise be read as one long one.
        /// </summary>
        private void Measure()
        {
            if (_measured || _frames.Count < 2) return;

            var stamps = new List<int>(_frames.Keys);
            stamps.Sort();

            int smallest = int.MaxValue;

            for (int i = 1; i < stamps.Count; i++)
            {
                int gap = stamps[i] - stamps[i - 1];
                if (gap > 0 && gap < smallest) smallest = gap;
            }

            if (smallest == int.MaxValue) return;

            _step = smallest;
            _measured = true;
        }

        /// <summary>
        /// Puts the clock back in touch with what is actually waiting.
        ///
        /// Two ways to lose it: overshooting a frame that is sitting right there, or
        /// falling so far behind that catching up one step at a time would take
        /// longer than the call.
        /// </summary>
        private void Realign()
        {
            if (_frames.Count == 0) return;

            int lowest = int.MaxValue;
            foreach (int timestamp in _frames.Keys)
                if (timestamp < lowest) lowest = timestamp;

            if (lowest < _next || lowest > _next + _step * LostCauseFrames)
                _next = lowest;
        }

        private void DropOldest()
        {
            int oldest = int.MaxValue;

            foreach (int timestamp in _frames.Keys)
                if (timestamp < oldest) oldest = timestamp;

            if (oldest == int.MaxValue) return;

            _frames.Remove(oldest);
            Late++;
        }
    }
}
