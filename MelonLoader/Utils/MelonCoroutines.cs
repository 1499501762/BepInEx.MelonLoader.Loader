using System;
using System.Collections;
using System.Collections.Generic;

namespace MelonLoader
{
    public class MelonCoroutines
    {
        internal static List<IEnumerator> _queue = new List<IEnumerator>();
        internal static bool _hasProcessed = false;

        /// <summary>
        /// Start a new coroutine.<br />
        /// Coroutines are called at the end of the game Update loops.
        /// </summary>
        /// <param name="routine">The target routine</param>
        /// <returns>An object that can be passed to Stop to stop this coroutine</returns>
        public static object Start(IEnumerator routine)
        {
            if (!_hasProcessed 
                || (SupportModule.Interface == null))
            {
                _queue.Add(routine);
                return routine;
            }

            return SupportModule.Interface.StartCoroutine(routine);
        }

        /// <summary>
        /// Stop a currently running coroutine
        /// </summary>
        /// <param name="coroutineToken">The coroutine to stop</param>
        public static void Stop(object coroutineToken)
        {
            if (!_hasProcessed
                || (SupportModule.Interface == null))
            {
                _queue.Remove(coroutineToken as IEnumerator);
                return;
            }

            SupportModule.Interface.StopCoroutine(coroutineToken);
        }
    }
}