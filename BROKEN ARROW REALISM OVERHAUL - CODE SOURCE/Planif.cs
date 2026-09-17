// RealismOverhaul - spreads the modules' periodic work over frames instead of letting it pile up in the same one.
//  Every module keeps its own period and its own conditions; it only asks for the frame's heavy slot when its timer is due.
//  The first module to ask in a frame gets it; the others keep their timer in the past and ask again at the next frame, so they run
//  one frame later and stay out of phase afterwards. A module that waited its maximum number of frames takes the frame's forced slot,
//  one job per frame there too, so nothing can starve and jobs that all reach their limit in the same frame still drain one by one:
//  a periodic job slips 3 frames (about 50 ms at 60 images per second, 150 ms at 20) plus its rank in that queue, and the first arming
//  of a battle 8 frames plus its rank (a few frames more with the modules of a battle start), which is what spreads the crash guards,
//  the preference writes and the hook installs of a battle start over frames instead of piling them into one.
//  No behaviour depends on this: it only chooses the frame a job runs in.
namespace RealismOverhaul
{
    static class Planif
    {
        internal const int WaitNormal = 3, WaitArm = 8;

        static int _frame, _used = -1, _forced = -1;

        /// Once at the top of Mod.OnUpdate.
        internal static void BeginFrame()
        {
            try { _frame = UnityEngine.Time.frameCount; }
            catch { _frame++; }
        }

        /// True when this job may run in this frame. waited holds the module's own count of frames spent waiting.
        /// Two slots per frame at most: the free one for the first asker, the forced one for a single job that reached its wait limit.
        internal static bool Take(ref int waited, int maxWait = WaitNormal)
        {
            if (_used != _frame) { _used = _frame; waited = 0; return true; }
            if (waited >= maxWait && _forced != _frame) { _forced = _frame; waited = 0; return true; }
            waited++;
            return false;
        }
    }
}
