// ShaderGraphUsageAnalyzer.cs
// Version: 1.000
//
// Reads a .shadergraph file and works out, for every exposed property and keyword, under which
// switch states it actually reaches the master stack. Switches are Enum / Boolean keyword nodes
// and Branch nodes whose predicate is a Boolean property. The result drives AutoHideShaderGraphGUI.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace VFX.Shaders.Editor
{
    /// <summary>
    /// Per-shader visibility rules built from the graph connections.
    /// </summary>
    internal sealed class ShaderGraphUsage
    {
        /// <summary>Material property name -> condition under which the property is used.</summary>
        public readonly Dictionary<string, ReachCondition> Rules = new Dictionary<string, ReachCondition>();

        /// <summary>Switch id -> how to read that switch from a material.</summary>
        public readonly Dictionary<string, GraphSwitch> Switches = new Dictionary<string, GraphSwitch>();

        /// <summary>Short type name of the active Universal sub target, e.g. UniversalUnlitSubTarget.</summary>
        public string SubTargetName = string.Empty;

        /// <summary>
        /// Returns false when the property is not used by any branch that the material currently selects.
        /// </summary>
        public bool IsUsed(string propertyName, Material material)
        {
            if (!Rules.TryGetValue(propertyName, out ReachCondition condition))
                return true;

            foreach (Dictionary<string, int> term in condition.Terms)
            {
                bool termMatches = true;
                foreach (KeyValuePair<string, int> requirement in term)
                {
                    int? current = Switches.TryGetValue(requirement.Key, out GraphSwitch graphSwitch)
                        ? graphSwitch.GetState(material)
                        : null;

                    // An unreadable switch never hides anything.
                    if (current.HasValue && current.Value != requirement.Value)
                    {
                        termMatches = false;
                        break;
                    }
                }

                if (termMatches)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// A keyword or Boolean property that selects between graph branches.
    /// </summary>
    internal sealed class GraphSwitch
    {
        public enum SwitchKind { EnumKeyword, BooleanKeyword, BooleanProperty }

        public SwitchKind Kind;
        public string ReferenceName;
        public string PropertyName;
        public string[] EntryKeywords;

        /// <summary>
        /// Current state: entry index for enums, 1 / 0 for booleans, or null when it can't be read.
        /// </summary>
        public int? GetState(Material material)
        {
            switch (Kind)
            {
                case SwitchKind.EnumKeyword:
                    for (int i = 0; i < EntryKeywords.Length; i++)
                    {
                        if (TryGetKeyword(material, EntryKeywords[i], out bool enabled) && enabled)
                            return i;
                    }

                    return ReadToggle(material, asIndex: true);

                case SwitchKind.BooleanKeyword:
                    if (TryGetKeyword(material, ReferenceName, out bool isOn))
                        return isOn ? 1 : 0;

                    return ReadToggle(material, asIndex: false);

                default:
                    return ReadToggle(material, asIndex: false);
            }
        }

        private int? ReadToggle(Material material, bool asIndex)
        {
            if (!material.HasFloat(PropertyName))
                return null;

            float value = material.GetFloat(PropertyName);
            if (asIndex)
                return Mathf.RoundToInt(value);
            return value != 0f ? 1 : 0;
        }

        private static bool TryGetKeyword(Material material, string name, out bool enabled)
        {
            LocalKeyword keyword = material.shader.keywordSpace.FindKeyword(name);
            enabled = keyword.isValid && material.IsKeywordEnabled(keyword);
            return keyword.isValid;
        }
    }

    /// <summary>
    /// A condition in disjunctive form: used when any term matches; a term is a set of switch == value requirements.
    /// No terms means "never used"; one empty term means "always used".
    /// </summary>
    internal sealed class ReachCondition
    {
        // Above this many terms the condition is treated as "always used" to keep analysis cheap.
        private const int MaxTerms = 64;

        public readonly List<Dictionary<string, int>> Terms = new List<Dictionary<string, int>>();

        // Switch id -> number of states it can take; shared by all conditions of one graph.
        private readonly IReadOnlyDictionary<string, int> stateCounts;

        public bool IsNever => Terms.Count == 0;
        public bool IsAlways => Terms.Exists(t => t.Count == 0);

        public ReachCondition(IReadOnlyDictionary<string, int> stateCounts)
        {
            this.stateCounts = stateCounts;
        }

        public static ReachCondition Always(IReadOnlyDictionary<string, int> stateCounts)
        {
            var condition = new ReachCondition(stateCounts);
            condition.Terms.Add(new Dictionary<string, int>());
            return condition;
        }

        /// <summary>Returns this condition narrowed by one more requirement.</summary>
        public ReachCondition With(string switchId, int value)
        {
            var result = new ReachCondition(stateCounts);
            foreach (Dictionary<string, int> term in Terms)
            {
                if (term.TryGetValue(switchId, out int existing))
                {
                    // The same switch can't hold two values at once, so that path is dead.
                    if (existing == value)
                        result.AddTerm(term);
                    continue;
                }

                var narrowed = new Dictionary<string, int>(term) { [switchId] = value };
                result.AddTerm(narrowed);
            }

            return result;
        }

        /// <summary>Adds every term of another condition (logical OR).</summary>
        public void Merge(ReachCondition other)
        {
            foreach (Dictionary<string, int> term in other.Terms)
            {
                if (IsAlways)
                    return;
                AddTerm(term);
            }
        }

        private void AddTerm(Dictionary<string, int> term)
        {
            if (term.Count == 0 || Terms.Count >= MaxTerms)
            {
                Terms.Clear();
                Terms.Add(new Dictionary<string, int>());
                return;
            }

            // Skip terms already covered by a looser one, and drop stricter ones this term covers.
            foreach (Dictionary<string, int> existing in Terms)
            {
                if (IsSubset(existing, term))
                    return;
            }

            Terms.RemoveAll(existing => IsSubset(term, existing));

            // When the other terms already cover every other state of one switch with the same
            // remaining requirements, that switch doesn't matter: add the looser term instead.
            Dictionary<string, int> looser = FindLooserTerm(term);
            if (looser != null)
            {
                AddTerm(looser);
                return;
            }

            Terms.Add(term);
        }

        private Dictionary<string, int> FindLooserTerm(Dictionary<string, int> term)
        {
            if (stateCounts == null)
                return null;

            foreach (KeyValuePair<string, int> requirement in term)
            {
                if (!stateCounts.TryGetValue(requirement.Key, out int stateCount))
                    continue;

                var rest = new Dictionary<string, int>(term);
                rest.Remove(requirement.Key);

                bool allStatesCovered = true;
                for (int state = 0; state < stateCount && allStatesCovered; state++)
                {
                    if (state != requirement.Value && !IsStateCovered(rest, requirement.Key, state))
                        allStatesCovered = false;
                }

                if (allStatesCovered)
                    return rest;
            }

            return null;
        }

        // True when an existing term holds for "rest AND switchId == state".
        private bool IsStateCovered(Dictionary<string, int> rest, string switchId, int state)
        {
            foreach (Dictionary<string, int> existing in Terms)
            {
                if (!existing.TryGetValue(switchId, out int value) || value != state)
                    continue;

                bool covered = true;
                foreach (KeyValuePair<string, int> pair in existing)
                {
                    if (pair.Key == switchId)
                        continue;
                    if (!rest.TryGetValue(pair.Key, out int restValue) || restValue != pair.Value)
                    {
                        covered = false;
                        break;
                    }
                }

                if (covered)
                    return true;
            }

            return false;
        }

        private static bool IsSubset(Dictionary<string, int> small, Dictionary<string, int> large)
        {
            if (small.Count > large.Count)
                return false;

            foreach (KeyValuePair<string, int> pair in small)
            {
                if (!large.TryGetValue(pair.Key, out int value) || value != pair.Value)
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Builds and caches <see cref="ShaderGraphUsage"/> for shader graph assets.
    /// </summary>
    internal static class ShaderGraphUsageAnalyzer
    {
        private const string KeywordNodeType = "KeywordNode";
        private const string BranchNodeType = "BranchNode";
        private const string PropertyNodeType = "PropertyNode";
        private const string BlockNodeType = "BlockNode";
        private const string InterpolatorReaderType = "CustomInterpolatorNode";
        private const string VertexDescriptorPrefix = "VertexDescription.";
        private const char InterpolatorDescriptorMark = '#';
        private const string KeywordObjectType = "ShaderKeyword";
        private const string BooleanPropertyType = "BooleanShaderProperty";
        private const string UniversalTargetType = "UniversalTarget";

        private const int KeywordTypeBoolean = 0;
        private const int KeywordTypeEnum = 1;
        private const int KeywordDefinitionPredefined = 2;

        // Slot ids as defined by Shader Graph's KeywordNode and BranchNode.
        private const int BooleanKeywordOnSlot = 1;
        private const int BooleanKeywordOffSlot = 2;
        private const int BranchPredicateSlot = 0;
        private const int BranchTrueSlot = 1;
        private const int BranchFalseSlot = 2;

        private const int BooleanStateCount = 2;

        private static readonly Dictionary<string, ShaderGraphUsage> Cache = new Dictionary<string, ShaderGraphUsage>();

        /// <summary>
        /// Returns the usage rules for a shader graph shader, or null if the shader is not a readable graph.
        /// </summary>
        public static ShaderGraphUsage Get(Shader shader)
        {
            if (shader == null)
                return null;

            string path = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
                return null;

            if (Cache.TryGetValue(path, out ShaderGraphUsage cached))
                return cached;

            ShaderGraphUsage usage = null;
            try
            {
                usage = Analyze(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AutoHide] Could not read '{path}': {e.Message}. All properties stay visible.");
            }

            Cache[path] = usage;
            return usage;
        }

        /// <summary>Forgets the cached rules of a graph so they are rebuilt on next use.</summary>
        public static void Invalidate(string path)
        {
            Cache.Remove(path);
        }

        // ---------- Graph model ----------

        // Fields below are filled by JsonUtility, which the compiler can't see.
#pragma warning disable CS0649
        [Serializable] private class JsonRef { public string m_Id; }
        [Serializable] private class JsonSlotRef { public JsonRef m_Node; public int m_SlotId; }
        [Serializable] private class JsonEdge { public JsonSlotRef m_OutputSlot; public JsonSlotRef m_InputSlot; }
        [Serializable] private class JsonEntry { public int id; public string referenceName; }

        // One class covers every object type in the file; JsonUtility fills only the fields present.
        [Serializable]
        private class JsonObject
        {
            public string m_Type;
            public string m_ObjectId;
            public JsonEdge[] m_Edges;
            public JsonRef m_Property;
            public JsonRef m_Keyword;
            public JsonRef m_ActiveSubTarget;
            public string m_DefaultReferenceName;
            public string m_OverrideReferenceName;
            public bool m_GeneratePropertyBlock;
            public int m_KeywordType;
            public int m_KeywordDefinition;
            public JsonEntry[] m_Entries;
            public string m_SerializedDescriptor;
            public string customBlockNodeName;

            public string ShortType => m_Type == null ? string.Empty : m_Type.Substring(m_Type.LastIndexOf('.') + 1);
            public string ReferenceName => string.IsNullOrEmpty(m_OverrideReferenceName) ? m_DefaultReferenceName : m_OverrideReferenceName;
        }
#pragma warning restore CS0649

        private sealed class Graph
        {
            public readonly Dictionary<string, JsonObject> Objects = new Dictionary<string, JsonObject>();
            public readonly List<JsonEdge> Edges = new List<JsonEdge>();
            public readonly Dictionary<string, List<JsonEdge>> EdgesFromNode = new Dictionary<string, List<JsonEdge>>();
            public readonly Dictionary<string, List<string>> InterpolatorReaders = new Dictionary<string, List<string>>();
            public readonly Dictionary<string, ReachCondition> Reach = new Dictionary<string, ReachCondition>();
            public readonly HashSet<string> InProgress = new HashSet<string>();
            public readonly Dictionary<string, int> StateCounts = new Dictionary<string, int>();

            public JsonObject Find(JsonRef reference)
            {
                return reference != null && reference.m_Id != null && Objects.TryGetValue(reference.m_Id, out JsonObject obj)
                    ? obj
                    : null;
            }
        }

        // ---------- Analysis ----------

        private static ShaderGraphUsage Analyze(string text)
        {
            Graph graph = Parse(text);
            var usage = new ShaderGraphUsage();

            foreach (JsonObject obj in graph.Objects.Values)
            {
                JsonObject subTarget = obj.ShortType == UniversalTargetType ? graph.Find(obj.m_ActiveSubTarget) : null;
                if (subTarget != null)
                    usage.SubTargetName = subTarget.ShortType;
            }

            // Combine reach conditions of every node that reads the same property or keyword.
            var inputReach = new Dictionary<string, ReachCondition>();
            foreach (JsonObject node in graph.Objects.Values)
            {
                JsonRef source = node.ShortType == PropertyNodeType ? node.m_Property
                    : node.ShortType == KeywordNodeType ? node.m_Keyword
                    : null;
                if (source == null || string.IsNullOrEmpty(source.m_Id))
                    continue;

                if (!inputReach.TryGetValue(source.m_Id, out ReachCondition combined))
                    inputReach[source.m_Id] = combined = new ReachCondition(graph.StateCounts);
                combined.Merge(GetReach(graph, usage, node.m_ObjectId));
            }

            foreach (KeyValuePair<string, ReachCondition> pair in inputReach)
            {
                // Unused and always-used inputs get no rule, so they are never hidden.
                if (pair.Value.IsNever || pair.Value.IsAlways)
                    continue;

                if (!graph.Objects.TryGetValue(pair.Key, out JsonObject input) || string.IsNullOrEmpty(input.ReferenceName))
                    continue;

                usage.Rules[MaterialPropertyName(input)] = pair.Value;
            }

            return usage;
        }

        private static ReachCondition GetReach(Graph graph, ShaderGraphUsage usage, string nodeId)
        {
            if (graph.Reach.TryGetValue(nodeId, out ReachCondition known))
                return known;

            graph.Objects.TryGetValue(nodeId, out JsonObject node);
            string interpolator = node != null && node.ShortType == BlockNodeType ? GetInterpolatorName(node) : null;
            if (node != null && node.ShortType == BlockNodeType && interpolator == null)
                return graph.Reach[nodeId] = ReachCondition.Always(graph.StateCounts);

            // A cycle can't feed the output through itself.
            if (!graph.InProgress.Add(nodeId))
                return new ReachCondition(graph.StateCounts);

            var result = new ReachCondition(graph.StateCounts);

            // A custom interpolator block only matters where the fragment stage reads it back.
            if (interpolator != null && graph.InterpolatorReaders.TryGetValue(interpolator, out List<string> readers))
            {
                foreach (string readerId in readers)
                {
                    result.Merge(GetReach(graph, usage, readerId));
                    if (result.IsAlways)
                        break;
                }
            }

            if (graph.EdgesFromNode.TryGetValue(nodeId, out List<JsonEdge> outgoing))
            {
                foreach (JsonEdge edge in outgoing)
                {
                    string consumerId = edge.m_InputSlot.m_Node.m_Id;
                    ReachCondition consumerReach = GetReach(graph, usage, consumerId);
                    if (consumerReach.IsNever)
                        continue;

                    if (TryGetSwitch(graph, usage, consumerId, edge.m_InputSlot.m_SlotId, out string switchId, out int value))
                        result.Merge(consumerReach.With(switchId, value));
                    else
                        result.Merge(consumerReach);

                    if (result.IsAlways)
                        break;
                }
            }

            graph.InProgress.Remove(nodeId);
            return graph.Reach[nodeId] = result;
        }

        /// <summary>
        /// Checks whether entering a node through the given input slot depends on a switch state.
        /// </summary>
        private static bool TryGetSwitch(Graph graph, ShaderGraphUsage usage, string nodeId, int slotId,
            out string switchId, out int value)
        {
            switchId = null;
            value = 0;
            if (!graph.Objects.TryGetValue(nodeId, out JsonObject node))
                return false;

            if (node.ShortType == KeywordNodeType)
            {
                JsonObject keyword = graph.Find(node.m_Keyword);
                if (keyword == null || !IsMaterialKeyword(keyword))
                    return false;

                switchId = RegisterKeywordSwitch(graph, usage, keyword);
                if (keyword.m_KeywordType == KeywordTypeBoolean)
                {
                    value = slotId == BooleanKeywordOnSlot ? 1 : 0;
                    return slotId == BooleanKeywordOnSlot || slotId == BooleanKeywordOffSlot;
                }

                // Enum keyword input slots use the entry id as their slot id.
                JsonEntry[] entries = keyword.m_Entries ?? Array.Empty<JsonEntry>();
                for (int i = 0; i < entries.Length; i++)
                {
                    if (entries[i].id == slotId)
                    {
                        value = i;
                        return true;
                    }
                }

                return false;
            }

            if (node.ShortType == BranchNodeType && (slotId == BranchTrueSlot || slotId == BranchFalseSlot))
            {
                JsonObject predicate = FindBooleanPredicate(graph, nodeId);
                if (predicate == null)
                    return false;

                switchId = "prop:" + predicate.ReferenceName;
                if (!usage.Switches.ContainsKey(switchId))
                {
                    usage.Switches[switchId] = new GraphSwitch
                    {
                        Kind = GraphSwitch.SwitchKind.BooleanProperty,
                        ReferenceName = predicate.ReferenceName,
                        PropertyName = predicate.ReferenceName
                    };
                    graph.StateCounts[switchId] = BooleanStateCount;
                }

                value = slotId == BranchTrueSlot ? 1 : 0;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the interpolator name of a custom interpolator block ("VertexDescription.Name#2#"), or null.
        /// </summary>
        private static string GetInterpolatorName(JsonObject block)
        {
            string descriptor = block.m_SerializedDescriptor;
            if (string.IsNullOrEmpty(descriptor) || !descriptor.StartsWith(VertexDescriptorPrefix, StringComparison.Ordinal))
                return null;

            int mark = descriptor.IndexOf(InterpolatorDescriptorMark);
            return mark > VertexDescriptorPrefix.Length
                ? descriptor.Substring(VertexDescriptorPrefix.Length, mark - VertexDescriptorPrefix.Length)
                : null;
        }

        /// <summary>Returns the Boolean property wired straight into a Branch predicate, if any.</summary>
        private static JsonObject FindBooleanPredicate(Graph graph, string branchId)
        {
            foreach (JsonEdge edge in graph.Edges)
            {
                if (edge.m_InputSlot.m_Node.m_Id != branchId || edge.m_InputSlot.m_SlotId != BranchPredicateSlot)
                    continue;

                JsonObject source = graph.Find(edge.m_OutputSlot.m_Node);
                JsonObject property = source != null && source.ShortType == PropertyNodeType ? graph.Find(source.m_Property) : null;
                if (property != null && property.ShortType == BooleanPropertyType && !string.IsNullOrEmpty(property.ReferenceName))
                    return property;
            }

            return null;
        }

        private static bool IsMaterialKeyword(JsonObject keyword)
        {
            // Only keywords the material can change act as switches; global or predefined ones stay neutral.
            return keyword.ShortType == KeywordObjectType && keyword.m_GeneratePropertyBlock &&
                   keyword.m_KeywordDefinition != KeywordDefinitionPredefined &&
                   !string.IsNullOrEmpty(keyword.ReferenceName) &&
                   (keyword.m_KeywordType == KeywordTypeBoolean || keyword.m_KeywordType == KeywordTypeEnum);
        }

        private static string RegisterKeywordSwitch(Graph graph, ShaderGraphUsage usage, JsonObject keyword)
        {
            string switchId = "kw:" + keyword.ReferenceName;
            if (usage.Switches.ContainsKey(switchId))
                return switchId;

            var graphSwitch = new GraphSwitch
            {
                ReferenceName = keyword.ReferenceName,
                PropertyName = MaterialPropertyName(keyword)
            };

            if (keyword.m_KeywordType == KeywordTypeBoolean)
            {
                graphSwitch.Kind = GraphSwitch.SwitchKind.BooleanKeyword;
                graph.StateCounts[switchId] = BooleanStateCount;
            }
            else
            {
                graphSwitch.Kind = GraphSwitch.SwitchKind.EnumKeyword;
                JsonEntry[] entries = keyword.m_Entries ?? Array.Empty<JsonEntry>();
                graphSwitch.EntryKeywords = new string[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                    graphSwitch.EntryKeywords[i] = keyword.ReferenceName + "_" + entries[i].referenceName;
                graph.StateCounts[switchId] = entries.Length;
            }

            usage.Switches[switchId] = graphSwitch;
            return switchId;
        }

        /// <summary>Name of the material property Shader Graph generates for a property or keyword.</summary>
        private static string MaterialPropertyName(JsonObject input)
        {
            string reference = input.ReferenceName;

            // Shader Graph exposes a Boolean keyword "_FOO_ON" as the material property "_FOO".
            if (input.ShortType == KeywordObjectType && input.m_KeywordType == KeywordTypeBoolean &&
                reference.EndsWith("_ON", StringComparison.Ordinal))
                return reference.Substring(0, reference.Length - 3);

            return reference;
        }

        // ---------- Parsing ----------

        private static Graph Parse(string text)
        {
            var graph = new Graph();
            foreach (string json in SplitObjects(text))
            {
                JsonObject obj;
                try
                {
                    obj = JsonUtility.FromJson<JsonObject>(json);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (obj == null || string.IsNullOrEmpty(obj.m_ObjectId))
                    continue;

                graph.Objects[obj.m_ObjectId] = obj;

                if (obj.ShortType == InterpolatorReaderType && !string.IsNullOrEmpty(obj.customBlockNodeName))
                {
                    if (!graph.InterpolatorReaders.TryGetValue(obj.customBlockNodeName, out List<string> readers))
                        graph.InterpolatorReaders[obj.customBlockNodeName] = readers = new List<string>();
                    readers.Add(obj.m_ObjectId);
                }

                if (obj.m_Edges == null)
                    continue;

                foreach (JsonEdge edge in obj.m_Edges)
                {
                    if (edge?.m_OutputSlot?.m_Node?.m_Id == null || edge.m_InputSlot?.m_Node?.m_Id == null)
                        continue;

                    graph.Edges.Add(edge);
                    string from = edge.m_OutputSlot.m_Node.m_Id;
                    if (!graph.EdgesFromNode.TryGetValue(from, out List<JsonEdge> list))
                        graph.EdgesFromNode[from] = list = new List<JsonEdge>();
                    list.Add(edge);
                }
            }

            return graph;
        }

        /// <summary>
        /// A .shadergraph file is a sequence of top-level JSON objects; this splits them apart.
        /// </summary>
        private static IEnumerable<string> SplitObjects(string text)
        {
            int depth = 0;
            int start = -1;
            bool inString = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\')
                        i++;
                    else if (c == '"')
                        inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    if (depth == 0)
                        start = i;
                    depth++;
                }
                else if (c == '}' && depth > 0)
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        yield return text.Substring(start, i - start + 1);
                        start = -1;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Drops cached rules when a shader graph is reimported, moved or deleted.
    /// </summary>
    internal sealed class ShaderGraphUsagePostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (string path in imported)
                ShaderGraphUsageAnalyzer.Invalidate(path);
            foreach (string path in deleted)
                ShaderGraphUsageAnalyzer.Invalidate(path);
            foreach (string path in movedFrom)
                ShaderGraphUsageAnalyzer.Invalidate(path);
        }
    }
}
