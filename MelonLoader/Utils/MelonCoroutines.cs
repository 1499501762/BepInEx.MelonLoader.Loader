using System;
using System.Collections;
using System.Collections.Generic;

namespace MelonLoader
{
    public class MelonCoroutines
    {
        internal static List<IEnumerator> _queue = new List<IEnumerator>();
        internal static bool _hasProcessed = false;

        private static readonly Dictionary<IEnumerator, float> _waitUntil = new Dictionary<IEnumerator, float>();

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
                _waitUntil.Remove(coroutineToken as IEnumerator);
                _stacks.Remove(coroutineToken as IEnumerator);
                return;
            }

            SupportModule.Interface.StopCoroutine(coroutineToken);
        }

        private static readonly Dictionary<IEnumerator, Stack<IEnumerator>> _stacks = new Dictionary<IEnumerator, Stack<IEnumerator>>();

        /// <summary>
        /// Advances coroutines queued while the native SupportModule coroutine runner is
        /// unavailable (i.e. under BepInEx hosting). Called once per frame by the host driver.
        /// Nested <see cref="IEnumerator"/> yields (e.g. <c>yield return Foo()</c> where Foo
        /// returns an iterator) are run to completion before resuming the parent, matching
        /// Unity/MelonLoader coroutine semantics.
        /// </summary>
        /// <param name="getWaitSeconds">Resolves a yielded object to seconds-to-wait, or
        /// <c>null</c> if the yield resumes next frame. The driver reflects on UnityEngine
        /// yield types (e.g. <c>WaitForSeconds</c>) to provide this.</param>
        /// <param name="currentTime">Current time in seconds (e.g. <c>UnityEngine.Time.time</c>).</param>
        public static void ProcessQueue(Func<object, float?> getWaitSeconds, float currentTime)
        {
            if (_queue.Count == 0)
                return;

            for (var i = _queue.Count - 1; i >= 0; i--)
            {
                var root = _queue[i];

                if (!_stacks.TryGetValue(root, out var stack))
                {
                    stack = new Stack<IEnumerator>();
                    stack.Push(root);
                    _stacks[root] = stack;
                }

                if (_waitUntil.TryGetValue(root, out var until))
                {
                    if (currentTime < until)
                        continue;
                    _waitUntil.Remove(root);
                }

                bool finished = false;
                bool stepped = false;
                while (stack.Count > 0 && !stepped)
                {
                    var top = stack.Peek();
                    bool done;
                    try
                    {
                        done = !top.MoveNext();
                    }
                    catch
                    {
                        done = true;
                    }

                    if (done)
                    {
                        stack.Pop();
                        if (stack.Count == 0)
                        {
                            finished = true;
                            break;
                        }

                        continue; // resume the parent coroutine this pass
                    }

                    var current = top.Current;
                    if (current is IEnumerator nested)
                    {
                        // Run the nested coroutine to completion before resuming the parent.
                        stack.Push(nested);
                        continue;
                    }

                    var wait = current == null ? (float?)null : getWaitSeconds(current);
                    if (wait.HasValue)
                        _waitUntil[root] = currentTime + wait.Value;

                    stepped = true;
                }

                if (finished)
                {
                    _queue.RemoveAt(i);
                    _stacks.Remove(root);
                    _waitUntil.Remove(root);
                }
            }
        }
    }
}