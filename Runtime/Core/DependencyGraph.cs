using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Yozolab.YoluPainter.Core
{
    public enum GraphNodeKind { Paint, Fill, Path, Mask, Filter, Generator, Anchor, Adjustment, Composite, ChannelOutput }
    public enum GraphValueType { Color, Scalar, TangentNormal }

    public sealed class GraphInput
    {
        public string Name { get; private set; }
        public GraphValueType Type { get; private set; }
        /// <summary>When set, enforces semantic channel equality as well as storage type.</summary>
        public PaintChannel? Channel { get; private set; }
        public GraphInput(string name, GraphValueType type, PaintChannel? channel = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Input name is required.", nameof(name));
            DependencyNode.ValidateType(type); if (channel.HasValue) PaintLayer.ValidateChannel(channel.Value);
            Name = name; Type = type; Channel = channel;
        }
    }

    /// <summary>Declarations for a future effect evaluator, not an implementation of those effects.
    /// Layer order increases bottom-to-top; effect order increases source-to-output within a layer.</summary>
    public sealed class DependencyNode
    {
        internal DependencyGraph Owner;
        private readonly Dictionary<string, GraphInput> inputs = new Dictionary<string, GraphInput>(StringComparer.Ordinal);
        public Guid Id { get; private set; }
        public Guid TextureSetId { get; private set; }
        public GraphNodeKind Kind { get; private set; }
        public GraphValueType OutputType { get; private set; }
        public PaintChannel? OutputChannel { get; private set; }
        public int LayerOrder { get; internal set; }
        public int EffectOrder { get; internal set; }
        public long Revision { get; internal set; }
        public bool IsGlobal { get; private set; }
        public int HaloPixels { get; private set; }
        public IReadOnlyDictionary<string, GraphInput> Inputs { get; private set; }
        public DependencyNode(Guid id, Guid textureSetId, GraphNodeKind kind, GraphValueType outputType,
            int layerOrder, int effectOrder, IEnumerable<GraphInput> inputs = null, PaintChannel? outputChannel = null,
            int haloPixels = 0, bool isGlobal = false)
        {
            if (id == Guid.Empty || textureSetId == Guid.Empty) throw new ArgumentException("Stable non-empty node and texture-set IDs are required.");
            if (!Enum.IsDefined(typeof(GraphNodeKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            ValidateType(outputType); if (outputChannel.HasValue) PaintLayer.ValidateChannel(outputChannel.Value);
            if (layerOrder < 0 || effectOrder < 0 || haloPixels < 0) throw new ArgumentOutOfRangeException("order/halo");
            Id = id; TextureSetId = textureSetId; Kind = kind; OutputType = outputType; OutputChannel = outputChannel;
            LayerOrder = layerOrder; EffectOrder = effectOrder; HaloPixels = haloPixels; IsGlobal = isGlobal;
            if (inputs != null) foreach (var input in inputs)
            {
                if (input == null) throw new ArgumentException("Null input port.", nameof(inputs));
                if (this.inputs.ContainsKey(input.Name)) throw new ArgumentException("Duplicate input port: " + input.Name, nameof(inputs));
                this.inputs.Add(input.Name, input);
            }
            Inputs = new ReadOnlyDictionary<string, GraphInput>(this.inputs);
        }
        internal static void ValidateType(GraphValueType type)
        { if (!Enum.IsDefined(typeof(GraphValueType), type)) throw new ArgumentOutOfRangeException(nameof(type)); }
    }

    public sealed class GraphConnection
    {
        public Guid SourceId { get; private set; }
        public Guid TargetId { get; private set; }
        public string InputName { get; private set; }
        internal GraphConnection(Guid source, Guid target, string input) { SourceId = source; TargetId = target; InputName = input; }
    }

    /// <summary>Typed, same-texture-set acyclic graph. Each input has at most one source. Connections must point
    /// from a lower layer or an earlier effect in the same layer. Rejected edits leave the graph unchanged.</summary>
    public sealed class DependencyGraph
    {
        private readonly Dictionary<Guid, DependencyNode> nodes = new Dictionary<Guid, DependencyNode>();
        private readonly List<GraphConnection> connections = new List<GraphConnection>();
        public IReadOnlyDictionary<Guid, DependencyNode> Nodes { get; private set; }
        public IReadOnlyList<GraphConnection> Connections { get; private set; }
        public DependencyGraph()
        { Nodes = new ReadOnlyDictionary<Guid, DependencyNode>(nodes); Connections = connections.AsReadOnly(); }
        public void AddNode(DependencyNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (nodes.ContainsKey(node.Id)) throw new ArgumentException("Duplicate node ID.", nameof(node));
            if (node.Owner != null) throw new InvalidOperationException("Node already belongs to a graph.");
            nodes.Add(node.Id, node); node.Owner = this;
        }
        public void Connect(Guid source, Guid target, string inputName)
        { string reason; if (!TryConnect(source, target, inputName, out reason)) throw new InvalidOperationException(reason); }
        public bool TryConnect(Guid source, Guid target, string inputName, out string reason)
        {
            DependencyNode sourceNode, targetNode;
            if (!nodes.TryGetValue(source, out sourceNode) || !nodes.TryGetValue(target, out targetNode))
            { reason = "Missing source or target node."; return false; }
            if (inputName == null) { reason = "Input port name is required."; return false; }
            GraphInput input;
            if (!targetNode.Inputs.TryGetValue(inputName, out input)) { reason = "Target input port does not exist."; return false; }
            foreach (var edge in connections) if (edge.TargetId == target && edge.InputName == inputName)
            { reason = "Input is already connected; disconnect explicitly before replacing its source."; return false; }
            if (source == target || HasPath(target, source)) { reason = "Connection would create a dependency cycle."; return false; }
            if (!ValidateEdge(sourceNode, targetNode, input, out reason)) return false;
            connections.Add(new GraphConnection(source, target, inputName)); Invalidate(target); reason = null; return true;
        }
        public bool Disconnect(Guid target, string inputName)
        {
            for (int i = 0; i < connections.Count; i++) if (connections[i].TargetId == target && connections[i].InputName == inputName)
            { connections.RemoveAt(i); Invalidate(target); return true; }
            return false;
        }
        /// <summary>Refuses removal of a referenced node; caller must rewire or explicitly disconnect consumers.</summary>
        public void RemoveNode(Guid id)
        {
            if (!nodes.ContainsKey(id)) throw new KeyNotFoundException("Node not found.");
            foreach (var edge in connections) if (edge.SourceId == id)
                throw new InvalidOperationException("Node has consumers. Reconnect, bake explicitly, or cancel deletion.");
            connections.RemoveAll(edge => edge.TargetId == id); nodes[id].Owner = null; nodes.Remove(id);
        }
        public bool TryMoveNode(Guid id, int layerOrder, int effectOrder, out string reason)
        {
            DependencyNode node;
            if (!nodes.TryGetValue(id, out node)) { reason = "Node not found."; return false; }
            if (layerOrder < 0 || effectOrder < 0) { reason = "Orders must be nonnegative."; return false; }
            int oldLayer = node.LayerOrder, oldEffect = node.EffectOrder;
            node.LayerOrder = layerOrder; node.EffectOrder = effectOrder;
            foreach (var edge in connections)
            {
                var source = nodes[edge.SourceId]; var target = nodes[edge.TargetId];
                if (!ValidateEdge(source, target, target.Inputs[edge.InputName], out reason))
                { node.LayerOrder = oldLayer; node.EffectOrder = oldEffect; return false; }
            }
            Invalidate(id); reason = null; return true;
        }
        /// <summary>Atomic multi-node order change for layer reorder. No partial reordering on rejection.</summary>
        public bool TrySetOrders(IReadOnlyDictionary<Guid, NodeOrder> orders, out string reason)
        {
            if (orders == null) throw new ArgumentNullException(nameof(orders));
            var previous = new Dictionary<Guid, NodeOrder>();
            foreach (var pair in orders)
            {
                DependencyNode node;
                if (!nodes.TryGetValue(pair.Key, out node)) { reason = "Node not found."; return false; }
                if (pair.Value.Layer < 0 || pair.Value.Effect < 0) { reason = "Orders must be nonnegative."; return false; }
                previous.Add(pair.Key, new NodeOrder(node.LayerOrder, node.EffectOrder));
            }
            foreach (var pair in orders) { nodes[pair.Key].LayerOrder = pair.Value.Layer; nodes[pair.Key].EffectOrder = pair.Value.Effect; }
            foreach (var edge in connections)
            {
                var source = nodes[edge.SourceId]; var target = nodes[edge.TargetId];
                if (!ValidateEdge(source, target, target.Inputs[edge.InputName], out reason))
                {
                    foreach (var pair in previous) { nodes[pair.Key].LayerOrder = pair.Value.Layer; nodes[pair.Key].EffectOrder = pair.Value.Effect; }
                    return false;
                }
            }
            foreach (var id in orders.Keys) Invalidate(id);
            reason = null; return true;
        }
        public IReadOnlyList<Guid> GetConsumers(Guid source)
        {
            if (!nodes.ContainsKey(source)) throw new KeyNotFoundException("Node not found.");
            var values = new HashSet<Guid>(); foreach (var edge in connections) if (edge.SourceId == source) values.Add(edge.TargetId);
            var result = new List<Guid>(values); result.Sort(); return result.AsReadOnly();
        }
        /// <summary>Monotonic revision propagation; hidden nodes remain part of this dependency graph.</summary>
        public IReadOnlyList<Guid> Invalidate(Guid source)
        {
            if (!nodes.ContainsKey(source)) throw new KeyNotFoundException("Node not found.");
            var visited = new HashSet<Guid>(); var pending = new Stack<Guid>(); pending.Push(source);
            while (pending.Count > 0)
            {
                Guid id = pending.Pop(); if (!visited.Add(id)) continue;
                nodes[id].Revision++;
                foreach (var edge in connections) if (edge.SourceId == id) pending.Push(edge.TargetId);
            }
            var result = new List<Guid>(); foreach (var node in TopologicalOrder()) if (visited.Contains(node.Id)) result.Add(node.Id);
            return result.AsReadOnly();
        }
        public IReadOnlyList<DependencyNode> TopologicalOrder()
        {
            // Strict layer/effect ordering is already a topological invariant; GUID resolves independent ties.
            var result = new List<DependencyNode>(nodes.Values);
            result.Sort((a, b) => { int layer = a.LayerOrder.CompareTo(b.LayerOrder); if (layer != 0) return layer;
                int effect = a.EffectOrder.CompareTo(b.EffectOrder); return effect != 0 ? effect : a.Id.CompareTo(b.Id); });
            return result.AsReadOnly();
        }
        private bool HasPath(Guid source, Guid target)
        {
            var seen = new HashSet<Guid>(); var stack = new Stack<Guid>(); stack.Push(source);
            while (stack.Count > 0)
            {
                Guid current = stack.Pop(); if (current == target) return true; if (!seen.Add(current)) continue;
                foreach (var edge in connections) if (edge.SourceId == current) stack.Push(edge.TargetId);
            }
            return false;
        }
        private static bool ValidateEdge(DependencyNode source, DependencyNode target, GraphInput input, out string reason)
        {
            if (source.TextureSetId != target.TextureSetId) { reason = "Cross-texture-set anchors are not supported."; return false; }
            if (source.OutputType != input.Type) { reason = "Incompatible input/output value types."; return false; }
            if (input.Channel.HasValue && source.OutputChannel != input.Channel) { reason = "Incompatible semantic channels."; return false; }
            if (source.LayerOrder > target.LayerOrder || (source.LayerOrder == target.LayerOrder && source.EffectOrder >= target.EffectOrder))
            { reason = "Source must be in a lower layer or an earlier effect in the same layer."; return false; }
            reason = null; return true;
        }
    }

    public readonly struct NodeOrder
    {
        public readonly int Layer, Effect;
        public NodeOrder(int layer, int effect) { Layer = layer; Effect = effect; }
    }
}
