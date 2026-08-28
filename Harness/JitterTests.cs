using System;
using Lumigram.Voip;

namespace Lumigram.Harness
{
    /// <summary>
    /// The jitter buffer, against the ways a network actually misbehaves.
    ///
    /// Every check here is a thing that sounds wrong rather than a thing that
    /// throws. A frame played out of order is a syllable in the wrong place; one
    /// played twice is a stutter; a buffer that never starts is silence on a call
    /// that says it is connected. None of those raise anything, so they have to be
    /// pinned down where they can be seen.
    /// </summary>
    internal static class JitterTests
    {
        private static int _checks;
        private static int _failures;

        private const int Frame = JitterBuffer.DefaultFrameDuration;

        public static bool RunAll()
        {
            _checks = 0;
            _failures = 0;

            Section("nothing plays until enough is held back");
            {
                var buffer = new JitterBuffer(Frame * 2, Frame * 10);

                buffer.Put(0, Frame_(1));
                Null("one frame is not enough", buffer.Get());

                buffer.Put(Frame, Frame_(2));
                Eq("the second starts playback", 1, First(buffer.Get()));
            }

            Section("the frame duration is measured, not assumed");
            {
                var buffer = new JitterBuffer(Frame * 2, Frame * 10);

                // libtgvoip's default is 60 ms. A buffer that assumes 20 looks for
                // timestamps that never arrive and plays nothing at all, while every
                // counter it keeps says the connection is fine.
                buffer.Put(1000, Frame_(1));
                buffer.Put(1060, Frame_(2));
                buffer.Put(1120, Frame_(3));

                Eq("first", 1, First(buffer.Get()));
                Eq("measured 60 ms", 60, buffer.FrameDuration);
                Eq("second", 2, First(buffer.Get()));
                Eq("third", 3, First(buffer.Get()));
            }

            Section("frames come out in order, whatever order they arrived in");
            {
                var buffer = Filled();

                // Arriving backwards is the ordinary case on a mobile network, and
                // it must not be audible at all.
                buffer.Put(Frame * 5, Frame_(6));
                buffer.Put(Frame * 3, Frame_(4));
                buffer.Put(Frame * 4, Frame_(5));

                Eq("first", 1, First(buffer.Get()));
                Eq("second", 2, First(buffer.Get()));
                Eq("third", 3, First(buffer.Get()));
                Eq("fourth", 4, First(buffer.Get()));
                Eq("fifth", 5, First(buffer.Get()));
                Eq("sixth", 6, First(buffer.Get()));
            }

            Section("a frame that missed its turn is dropped, not played late");
            {
                var buffer = Filled();

                buffer.Get();
                buffer.Get();

                int late = buffer.Late;
                buffer.Put(0, Frame_(99));

                Eq("counted as late", late + 1, buffer.Late);
                Eq("still playing in order", 3, First(buffer.Get()));
            }

            Section("a duplicate is ignored");
            {
                var buffer = Filled();

                buffer.Put(Frame * 3, Frame_(4));
                buffer.Put(Frame * 3, Frame_(44));

                buffer.Get();
                buffer.Get();
                buffer.Get();

                Eq("the first copy wins", 4, First(buffer.Get()));
            }

            Section("a lost frame leaves a gap and time moves on");
            {
                var buffer = new JitterBuffer(Frame * 2, Frame * 10);

                buffer.Put(0, Frame_(1));
                buffer.Put(Frame, Frame_(2));
                buffer.Put(Frame * 3, Frame_(4));      // the third never arrives

                Eq("first", 1, First(buffer.Get()));
                Eq("second", 2, First(buffer.Get()));
                Null("the gap", buffer.Get());
                Eq("counted as missing", 1, buffer.Missing);

                // The frame after the gap must still be on time. Waiting for what
                // was lost would make everything after it late too.
                Eq("and playback carries on", 4, First(buffer.Get()));
            }

            Section("starving does not run the clock past the sender");
            {
                var buffer = new JitterBuffer(Frame * 2, Frame * 10);

                buffer.Put(0, Frame_(1));
                buffer.Put(Frame, Frame_(2));

                Eq("first", 1, First(buffer.Get()));
                Eq("second", 2, First(buffer.Get()));

                // The player pulls faster than the network delivers, so it asks
                // several times with nothing there. Advancing on each of those would
                // put the clock ahead of everything still in flight - and then every
                // frame that arrives is "late", is discarded, and the call goes
                // permanently silent while the packets keep coming.
                for (int i = 0; i < 10; i++) Null("starved", buffer.Get());

                int late = buffer.Late;

                buffer.Put(Frame * 2, Frame_(3));
                buffer.Put(Frame * 3, Frame_(4));

                Eq("nothing was thrown away", late, buffer.Late);
                Eq("and playback carries on", 3, First(buffer.Get()));
                Eq("with the next one too", 4, First(buffer.Get()));
            }

            Section("the delay grows when the connection keeps missing");
            {
                var buffer = new JitterBuffer(Frame * 2, Frame * 10);
                int started = buffer.DelayMs;

                buffer.Put(0, Frame_(1));
                buffer.Put(Frame, Frame_(2));

                buffer.Get();
                buffer.Get();

                // Frames arriving with holes torn in them, rather than nothing
                // arriving at all. This is the network losing packets, which is the
                // thing more delay actually helps with - as opposed to starvation,
                // which it only makes worse.
                buffer.Put(Frame * 5, Frame_(6));
                buffer.Put(Frame * 6, Frame_(7));
                buffer.Put(Frame * 7, Frame_(8));

                for (int i = 0; i < 3; i++) buffer.Get();

                Eq("it held more back", true, buffer.DelayMs > started);
            }

            Section("a runaway sender cannot grow the buffer without limit");
            {
                var buffer = new JitterBuffer(Frame * 2, Frame * 10);

                for (int i = 0; i < 500; i++)
                    buffer.Put(Frame * i, Frame_(i & 0x7f));

                Eq("bounded", true, buffer.Count <= 64);
            }

            Section("reset puts it back to the start");
            {
                var buffer = Filled();
                buffer.Get();

                buffer.Reset();

                Eq("empty", 0, buffer.Count);
                Null("and not playing", buffer.Get());
            }

            Console.WriteLine();
            Console.WriteLine("  {0} checks, {1} failures", _checks, _failures);
            return _failures == 0;
        }

        /// <summary>A buffer with three frames in it, ready to play.</summary>
        private static JitterBuffer Filled()
        {
            var buffer = new JitterBuffer(Frame * 2, Frame * 10);

            buffer.Put(0, Frame_(1));
            buffer.Put(Frame, Frame_(2));
            buffer.Put(Frame * 2, Frame_(3));

            return buffer;
        }

        /// <summary>A frame whose first byte identifies it.</summary>
        private static byte[] Frame_(int id)
        {
            return new byte[] { (byte)id, 0x55, 0xaa };
        }

        private static int First(byte[] frame)
        {
            return frame == null ? -1 : frame[0];
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("  [{0}]", title);
        }

        private static void Eq(string what, object expected, object actual)
        {
            _checks++;
            if (Equals(expected, actual)) return;

            Fail(what, "expected " + expected + ", got " + actual);
        }

        private static void Null(string what, object actual)
        {
            _checks++;
            if (actual == null) return;

            Fail(what, "expected nothing, got something");
        }

        private static void Fail(string what, string detail)
        {
            _failures++;
            Console.WriteLine("    FAIL {0}: {1}", what, detail);
        }
    }
}
