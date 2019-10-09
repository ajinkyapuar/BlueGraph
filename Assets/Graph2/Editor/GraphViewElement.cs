﻿
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEditor.Experimental.GraphView;

namespace Graph2
{
    /// <summary>
    /// Required concrete implementation of a GraphView
    /// </summary>
    class NodeGraphView : GraphView
    {
        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            var compatiblePorts = new List<Port>();
            var startPortView = startPort as PortView;

            ports.ForEach((port) => {
                var portView = port as PortView;
                if (portView.IsCompatibleWith(startPortView))
                {
                    compatiblePorts.Add(portView);
                }
            });
            
            return compatiblePorts;
        }
    }

    public class GraphViewElement : VisualElement
    {
        GraphEditor m_GraphEditor;
        Graph m_Graph;

        NodeGraphView m_GraphView;
        SearchProvider m_SearchProvider;
        EditorWindow m_EditorWindow;

        EdgeConnectorListener m_EdgeListener;
        
        HashSet<NodeView> m_DirtyNodes = new HashSet<NodeView>();

        public GraphViewElement(EditorWindow window)
        {
            m_EditorWindow = window;

            // TODO: Less hardcoded of a path
            StyleSheet styles = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Assets/Graph/Editor/Styles/GraphViewElement.uss"
            );
        
            styleSheets.Add(styles);
            
            m_EdgeListener = new EdgeConnectorListener(this);
            m_SearchProvider = ScriptableObject.CreateInstance<SearchProvider>();
            m_SearchProvider.graphView = this;

            CreateGraph();
        
            RegisterCallback<GeometryChangedEvent>(OnFirstResize);
        }
        
        /// <summary>
        /// Event handler to frame the graph view on initial layout
        /// </summary>
        private void OnFirstResize(GeometryChangedEvent evt)
        {
            UnregisterCallback<GeometryChangedEvent>(OnFirstResize);
            m_GraphView.FrameAll();
        }

        private GraphViewChange OnGraphViewChanged(GraphViewChange change)
        {
            Debug.Log("Change: " + change.ToString());

            // Dirty moved elements to update the target assets
            if (change.movedElements != null)
            {
                foreach (var element in change.movedElements)
                {
                    if (element is NodeView)
                    {
                        Dirty(element as NodeView);
                    }
                }
            }
            
            if (change.elementsToRemove != null)
            {
                foreach (var element in change.elementsToRemove)
                {
                    if (element is NodeView)
                    {
                        Debug.Log("Destroy node");
                        DestroyNode(element as NodeView);
                    }
                    else if (element is Edge)
                    {
                        Debug.Log("Destroy edge");
                        DestroyEdge(element as Edge);
                    }
                }
                
                // Save the batch of changes all at once
                AssetDatabase.SaveAssets();
            }

            return change;
        }

        /// <summary>
        /// Create and configure a new graph window for node editing
        /// </summary>
        private void CreateGraph()
        {
            m_GraphView = new NodeGraphView();
            m_GraphView.SetupZoom(0.05f, ContentZoomer.DefaultMaxScale);
        
            // Manipulators for the graph view itself
            m_GraphView.AddManipulator(new ContentDragger());
            m_GraphView.AddManipulator(new SelectionDragger());
            m_GraphView.AddManipulator(new RectangleSelector());
            m_GraphView.AddManipulator(new ClickSelector());
        
            // Add event handlers for shortcuts and changes
            m_GraphView.RegisterCallback<KeyDownEvent>(OnGraphKeydown);
            m_GraphView.graphViewChanged = OnGraphViewChanged;
            
            m_GraphView.nodeCreationRequest = (ctx) => OpenSearch(ctx.screenMousePosition);
        
            // Add handlers for (de)serialization
            m_GraphView.serializeGraphElements = OnSerializeGraphElements;
            m_GraphView.canPasteSerializedData = OnTryPasteSerializedData;
            m_GraphView.unserializeAndPaste = OnUnserializeAndPaste;
            
            VisualElement content = new VisualElement { name = "Content" };
            content.Add(m_GraphView);
        
            Add(content);
        }
       
        private void OnGraphKeydown(KeyDownEvent evt)
        {
            // TODO: Mac support

            // Group selected nodes
            if (evt.modifiers.HasFlag(EventModifiers.Control) && evt.keyCode == KeyCode.G)
            {
                GroupSelection();
            }
        
            // Other ideas:
            // - add comment node shortcut
            // - 
        }
        
        public void Load(Graph graph)
        {
            Debug.Log("Load graph");
            m_Graph = graph;
            
            AddNodes(graph.nodes);
            AddGroups(graph.groups);
        }
        
        public void CreateNode(Type type, Vector2 screenPosition, PortView connectedPort = null)
        {
            var windowRoot = m_EditorWindow.rootVisualElement;
            var windowMousePosition = m_EditorWindow.rootVisualElement.ChangeCoordinatesTo(
                windowRoot.parent, 
                screenPosition - m_EditorWindow.position.position
            );
        
            var graphMousePosition = m_GraphView.contentViewContainer.WorldToLocal(windowMousePosition);
        
            var typeData = NodeReflection.GetNodeType(type);
            var node = m_Graph.AddNode(type);
            node.name = typeData.name;
            node.position = graphMousePosition;
        
            // Add a node to the visual graph
            var editorType = NodeReflection.GetNodeEditorType(type);
            var element = Activator.CreateInstance(editorType) as NodeView;
            element.Initialize(node, m_EdgeListener);

            m_GraphView.AddElement(element);
            
            AssetDatabase.AddObjectToAsset(node, m_Graph);
            AssetDatabase.SaveAssets();
            
            // If there was a provided existing port to connect to, find the best 
            // candidate port on the new node and connect. 
            if (connectedPort != null)
            {
                var edge = new Edge();

                if (connectedPort.direction == Direction.Input)
                {
                    edge.input = connectedPort;
                    edge.output = element.GetCompatibleOutputPort(connectedPort);
                }
                else
                {
                    edge.output = connectedPort;
                    edge.input = element.GetCompatibleInputPort(connectedPort);
                }
                
                ConnectNodes(edge);
            }
            
            Dirty(element);
        }

        /// <summary>
        /// Remove a node from both the graph and the linked asset
        /// </summary>
        /// <param name="node"></param>
        public void DestroyNode(NodeView node)
        {
            m_Graph.RemoveNode(node.NodeData);
            ScriptableObject.DestroyImmediate(node.NodeData, true);
        }

        public void ConnectNodes(Edge edge)
        {
            if (edge.input == null || edge.output == null) return;
            
            // Handle single connection ports on either end. 
            var edgesToRemove = new List<GraphElement>();
            if (edge.input.capacity == Port.Capacity.Single)
            {
                foreach (var conn in edge.input.connections)
                {
                    edgesToRemove.Add(conn);
                }
            }

            if (edge.output.capacity == Port.Capacity.Single)
            {
                foreach (var conn in edge.input.connections)
                {
                    edgesToRemove.Add(conn);
                }
            }

            if (edgesToRemove.Count > 0)
            {
                m_GraphView.DeleteElements(edgesToRemove);
            }

            var newEdge = edge.input.ConnectTo(edge.output);
            m_GraphView.AddElement(newEdge);
            
            Dirty(edge.input.node as NodeView);
            Dirty(edge.output.node as NodeView);
        }

        /// <summary>
        /// Mark a node and all dependents as dirty for the next refresh. 
        /// </summary>
        /// <param name="node"></param>
        public void Dirty(NodeView node)
        {
            m_DirtyNodes.Add(node);

            foreach (var port in node.OutputPorts.Values)
            {
                foreach (var conn in port.connections)
                {
                    Dirty(conn.input.node as NodeView);
                }
            }
        }

        public void Update()
        {
            // Propagate change on dirty nodes
            foreach (var node in m_DirtyNodes)
            {
                node.OnUpdate();
            }
            
            m_DirtyNodes.Clear();
        }

        public void DestroyEdge(Edge edge)
        {
            var input = edge.input.node as NodeView;
            var output = edge.output.node as NodeView;

            edge.input.Disconnect(edge);
            edge.output.Disconnect(edge);

            edge.input = null;
            edge.output = null;

            m_GraphView.RemoveElement(edge);

            Dirty(input);
            Dirty(output);
        }

        public void OpenSearch(Vector2 screenPosition, PortView connectedPort = null)
        {
            m_SearchProvider.connectedPort = connectedPort;
            SearchWindow.Open(new SearchWindowContext(screenPosition), m_SearchProvider);
        }
        
        /// <summary>
        /// Handler for deserializing a node from a string payload
        /// </summary>
        /// <param name="operationName"></param>
        /// <param name="data"></param>
        private void OnUnserializeAndPaste(string operationName, string data)
        {
            Debug.Log("Operation name: " + operationName);

            var graph = CopyPasteGraph.Deserialize(data);
            
            // Add each node to the working graph
            foreach (var node in graph.nodes)
            {
                m_Graph.AddNode(node);
                AssetDatabase.AddObjectToAsset(node, m_Graph);
            }
            
            AssetDatabase.SaveAssets();

            // Add the new nodes and select them
            m_GraphView.ClearSelection();
            AddNodes(graph.nodes, true);
        }

        /// <summary>de
        /// Append nodes from a Graph onto the viewport
        /// </summary>
        /// <param name="graph"></param>
        private void AddNodes(List<AbstractNode> nodes, bool selectOnceAdded = false)
        {
            // Add views of each node from the graph
            Dictionary<AbstractNode, NodeView> nodeMap = new Dictionary<AbstractNode, NodeView>();
            foreach (var node in nodes)
            {
                var editorType = NodeReflection.GetNodeEditorType(node.GetType());
                var element = Activator.CreateInstance(editorType) as NodeView;
                element.Initialize(node, m_EdgeListener);
                m_GraphView.AddElement(element);

                nodeMap.Add(node, element);
                Dirty(element);
                
                if (selectOnceAdded)
                {
                    m_GraphView.AddToSelection(element);
                }
            }
            
            // Sync edges on the graph with our graph's connections 
            // TODO: Deal with trash connections from bad imports
            foreach (var node in nodeMap)
            {
                foreach (var port in node.Key.inputs)
                {
                    foreach (var conn in port.connections)
                    {
                        // Only add if the linked node is in the collection
                        if (nodeMap.ContainsKey(conn.node))
                        {
                            var inPort = node.Value.GetInputPort(port.portName);
                            var outPort = nodeMap[conn.node].GetOutputPort(conn.portName);
                        
                            if (inPort == null)
                            {
                                Debug.LogError($"Could not connect `{node.Value.title}:{port.portName}` -> `{conn.node.name}:{conn.portName}`. Input port `{port.portName}` no longer exists.");
                            }
                            else if (outPort == null)
                            {
                                Debug.LogError($"Could not connect `{conn.node.name}:{conn.portName}` to `{node.Value.name}:{port.portName}`. Output port `{conn.portName}` no longer exists.");
                            }
                            else
                            {
                                var edge = inPort.ConnectTo(outPort);
                                m_GraphView.AddElement(edge);
                            }
                        }
                    }
                }
            }
        }

        private NodeView GetNodeElement(AbstractNode node)
        {
            return m_GraphView.GetNodeByGuid(node.guid) as NodeView;
        }

        /// <summary>
        /// Create views for a set of NodeGroups
        /// </summary>
        /// <param name="groups"></param>
        private void AddGroups(List<NodeGroup> groups)
        { 
            foreach (var group in groups)
            {
                var groupView = new GroupView(group);
                
                foreach (var node in group.nodes)
                {
                    groupView.AddElement(GetNodeElement(node));
                }
                
                m_GraphView.AddElement(groupView);
            }
        }

        private void GroupSelection()
        {
            if (m_GraphView.selection.Count < 0)
            {
                return;
            }

            var group = new NodeGroup();
            group.title = "New Group";
            m_Graph.groups.Add(group);
            
            var groupView = new GroupView(group); 
            foreach (var node in m_GraphView.selection)
            {
                if (node is NodeView)
                {
                    var nodeView = node as NodeView;
                    groupView.AddElement(nodeView);
                }
            }
            
            m_GraphView.AddElement(groupView);
        }

        private bool OnTryPasteSerializedData(string data)
        {
            return CopyPasteGraph.CanDeserialize(data);
        }

        /// <summary>
        /// Serialize a selection to support cut/copy/duplicate
        /// </summary>
        private string OnSerializeGraphElements(IEnumerable<GraphElement> elements)
        {
            return CopyPasteGraph.Serialize(elements);;
        }
    }
}
