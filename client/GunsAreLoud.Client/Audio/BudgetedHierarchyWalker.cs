using System;
using System.Collections.Generic;

namespace GunsAreLoud.Client.Audio
{
    // A step visits one node, takes one child, or pops one frame. Wide and deep
    // hierarchies both obey the same limit; no recursive subtree work is hidden.
    internal sealed class BudgetedHierarchyWalker<T>
    {
        private struct Frame
        {
            internal T Node;
            internal int Child;
        }

        private readonly List<Frame> _stack = new List<Frame>();
        private readonly Func<T, bool> _alive;
        private readonly Func<T, int> _childCount;
        private readonly Func<T, int, T> _child;
        private readonly Action<T> _visit;
        private IReadOnlyList<T> _roots;
        private int _root;

        internal BudgetedHierarchyWalker(Func<T, bool> alive, Func<T, int> childCount,
            Func<T, int, T> child, Action<T> visit)
        {
            _alive = alive; _childCount = childCount; _child = child; _visit = visit;
        }

        internal bool Complete => _stack.Count == 0 && (_roots == null || _root >= _roots.Count);

        internal void Reset(IReadOnlyList<T> roots)
        {
            _stack.Clear(); _roots = roots; _root = 0;
        }

        internal int Run(int maxSteps, long deadline, Func<long> timestamp)
        {
            int steps = 0;
            while (steps < maxSteps && !Complete && timestamp() < deadline)
            {
                Step();
                steps++;
            }
            return steps;
        }

        private void Step()
        {
            if (_stack.Count == 0)
            {
                _stack.Add(new Frame { Node = _roots[_root++], Child = -1 });
                return;
            }
            int top = _stack.Count - 1;
            Frame frame = _stack[top];
            if (!_alive(frame.Node)) { _stack.RemoveAt(top); return; }
            if (frame.Child < 0)
            {
                frame.Child = 0;
                _stack[top] = frame;
                _visit(frame.Node);
                return;
            }
            if (frame.Child >= _childCount(frame.Node)) { _stack.RemoveAt(top); return; }
            T child = _child(frame.Node, frame.Child++);
            _stack[top] = frame;
            _stack.Add(new Frame { Node = child, Child = -1 });
        }
    }
}
