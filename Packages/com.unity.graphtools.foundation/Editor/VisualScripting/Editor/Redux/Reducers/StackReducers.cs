using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.EditorCommon.Extensions;
using UnityEditor.VisualScripting.Editor.SmartSearch;
using UnityEditor.VisualScripting.GraphViewModel;
using UnityEditor.VisualScripting.Model;
using UnityEditor.VisualScripting.Model.Stencils;
using UnityEngine;

namespace UnityEditor.VisualScripting.Editor
{
    static class StackReducers
    {
        static readonly Vector2 k_StackedTestNodesTokenOffset = new Vector2(100, 0);

        public static void Register(Store store)
        {
            store.Register<CreateStacksForNodesAction>(CreateStacksForNodes);
            store.Register<CreateLogNodeAction>(CreateLogNode);
            store.Register<MoveStackedNodesAction>(MoveStackedNodes);
            store.Register<SplitStackAction>(SplitStack);
            store.Register<MergeStackAction>(MergeStack);
            store.Register<ChangeStackedNodeAction>(ChangeStackedNode);
            store.Register<CreateStackedNodeFromSearcherAction>(CreateStackedNodeFromSearcher);
        }

        static State CreateStackedNodeFromSearcher(State previousState,
            CreateStackedNodeFromSearcherAction action)
        {
            var createdStackModels = action.SelectedItem.CreateElements.Invoke(
                new StackNodeCreationData(action.StackModel, action.Index, guids: action.Guids));

            if (createdStackModels?.FirstOrDefault() is INodeModel node)
                AnalyticsHelper.Instance.SetLastNodeCreated(node.Guid, node.Title);

            return previousState;
        }

        static State CreateStacksForNodes(State previousState, CreateStacksForNodesAction action)
        {
            VSGraphModel graphModel = (VSGraphModel)previousState.CurrentGraphModel;
            var affectedStacks = action.Stacks.SelectMany(stackOption => stackOption.NodeModels).OfType<NodeModel>()
                .Select(model => model.ParentStackModel).Distinct();
            foreach (var stack in affectedStacks)
                Undo.RegisterCompleteObjectUndo(stack.NodeAssetReference, "Move Stacked Node(s)");

            foreach (var stackOptions in action.Stacks)
            {
                var stack = graphModel.CreateStack(string.Empty, stackOptions.Position);
                if (stackOptions.NodeModels != null)
                    stack.MoveStackedNodes(stackOptions.NodeModels, 0);
            }
            return previousState;
        }

        static State CreateLogNode(State previousState, CreateLogNodeAction action)
        {
            VSGraphModel graphModel = (VSGraphModel)previousState.CurrentGraphModel;
            var stackModel = (StackBaseModel)action.StackModel;
            var functionNode = stackModel.CreateStackedNode<LogNodeModel>(LogNodeModel.NodeTitle);
            functionNode.LogType = action.LogType;

            IConstantNodeModel constantNode = graphModel.CreateConstantNode(
                "",
                typeof(string).GenerateTypeHandle(graphModel.Stencil),
                functionNode.Position - k_StackedTestNodesTokenOffset);
            ((ConstantNodeModel<string>)constantNode).value = $"{graphModel.NodeModels.Count}";
            graphModel.CreateEdge(functionNode.InputPort, constantNode.OutputPort);

            return previousState;
        }

        static State ChangeStackedNode(State previousState, ChangeStackedNodeAction action)
        {
            var graphModel = ((VSGraphModel)previousState.CurrentGraphModel);

            // Remove old node
            int index = -1;
            if (action.OldNodeModel != null)
                index = action.StackModel.NodeModels.IndexOf(action.OldNodeModel);

            // Add new node
            action.SelectedItem.CreateElements.Invoke(new StackNodeCreationData(action.StackModel, index));

            // Reconnect edges
            var newNodeModel = action.StackModel.NodeModels.ElementAt(index);
            if (action.OldNodeModel != null)
            {
                Undo.RegisterCompleteObjectUndo(graphModel, "Change Stacked Node");
                var oldInputs = action.OldNodeModel.InputsByDisplayOrder.ToList();
                var newInputs = newNodeModel.InputsByDisplayOrder.ToList();
                for (var i = 0; i < oldInputs.Count; ++i)
                {
                    IPortModel oldInputPort = oldInputs[i];
                    if (i < newInputs.Count)
                    {
                        foreach (var edge in graphModel.GetEdgesConnections(oldInputPort).Cast<EdgeModel>())
                        {
                            edge.SetFromPortModels(newInputs[i], edge.OutputPortModel);
                        }
                    }
                    else
                    {
                        var edges = graphModel.GetEdgesConnections(oldInputPort);
                        graphModel.DeleteEdges(edges);
                        break;
                    }
                }

                // delete after edge patching or undo/redo will fail
                var parentStack = (StackBaseModel)action.StackModel;
                graphModel.DeleteNode(action.OldNodeModel, GraphModel.DeleteConnections.False);

                if (parentStack.Capabilities.HasFlag(CapabilityFlags.DeletableWhenEmpty) &&
                    parentStack != (StackBaseModel)action.StackModel &&
                    !parentStack.NodeModels.Any())
                    graphModel.DeleteNode(parentStack, GraphModel.DeleteConnections.True);
            }
            return previousState;
        }

        static State MoveStackedNodes(State previousState, MoveStackedNodesAction action)
        {
            Undo.RegisterCompleteObjectUndo(action.StackModel.NodeAssetReference, "Move stacked nodes");
            ((StackBaseModel)action.StackModel).MoveStackedNodes(action.NodeModels, action.Index);
            return previousState;
        }

        static State SplitStack(State previousState, SplitStackAction action)
        {
            VSGraphModel graphModel = (VSGraphModel)previousState.CurrentGraphModel;

            if (action.SplitIndex > 0 && action.SplitIndex < action.StackModel.NodeModels.Count())
            {
                // Get the list of nodes to move to another stack.
                var nodeModels = action.StackModel.NodeModels.Skip(action.SplitIndex).ToList();
                if (nodeModels.Any())
                {
                    // Get old stack (stack A)
                    var stackA = action.StackModel;

                    // Create new stack (stack B).
                    var stackB = graphModel.CreateStack(((NodeModel)stackA).Title + "_split", stackA.Position + Vector2.up * 300);

                    // Move the list of nodes to this new stack.
                    Undo.RegisterCompleteObjectUndo(stackA.NodeAssetReference, "Move stacked nodes");
                    Undo.RegisterCompleteObjectUndo(stackB.NodeAssetReference, "Move stacked nodes");
                    stackB.MoveStackedNodes(nodeModels, 0);

                    // if the stack had a condition node or anything providing the actual port models, we need to move
                    // the nodes BEFORE fetching the port models, as stack.portModels will actually return the condition
                    // port models
                    var stackAOutputPortModel = stackA.OutputPorts.First();
                    var stackBInputPortModel = stackB.InputPorts.First();
                    var stackBOutputPortModel = stackB.OutputPorts.First();

                    // Connect the edges that were connected to the old stack to the new one.
                    var previousEdgeConnections = graphModel.GetEdgesConnections(stackAOutputPortModel).ToList();
                    foreach (var edge in previousEdgeConnections)
                    {
                        graphModel.CreateEdge(edge.InputPortModel, stackBOutputPortModel);
                        graphModel.DeleteEdge(edge);
                    }

                    // Connect the new stack with the old one.
                    IEdgeModel newEdge = graphModel.CreateEdge(stackBInputPortModel, stackAOutputPortModel);

                    graphModel.LastChanges.ChangedElements.Add(stackA);
                    graphModel.LastChanges.ModelsToAutoAlign.Add(newEdge);
                }
            }

            return previousState;
        }

        static State MergeStack(State previousState, MergeStackAction action)
        {
            VSGraphModel graphModel = (VSGraphModel)previousState.CurrentGraphModel;
            var stackModelA = (StackBaseModel)action.StackModelA;
            var stackModelB = (StackBaseModel)action.StackModelB;

            // Move all nodes from stackB to stackA
            Undo.RegisterCompleteObjectUndo(stackModelA.NodeAssetReference, "Move stacked nodes");
            Undo.RegisterCompleteObjectUndo(stackModelB.NodeAssetReference, "Move stacked nodes");
            stackModelA.MoveStackedNodes(stackModelB.NodeModels.ToList(), -1, false);

            // Move output connections of stackB to stackA
            var previousEdgeConnections = graphModel.GetEdgesConnections(stackModelB.OutputPorts.First()).ToList();
            foreach (var edge in previousEdgeConnections)
            {
                graphModel.CreateEdge(edge.InputPortModel, stackModelA.OutputPorts.First());
            }

            // Delete stackB
            graphModel.DeleteNode(stackModelB, GraphModel.DeleteConnections.True);

            previousState.MarkForUpdate(UpdateFlags.GraphTopology);

            return previousState;
        }
    }
}
