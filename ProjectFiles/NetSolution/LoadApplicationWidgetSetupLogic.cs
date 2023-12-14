#region Using directives
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.Core;
using FTOptix.CoreBase;
using FTOptix.NetLogic;
using FTOptix.DataLogger;
using FTOptix.Store;
using FTOptix.WebUI;
#endregion

public class LoadApplicationWidgetSetupLogic : BaseNetLogic
{
    private const string LOG_CATEGORY = nameof(LoadApplicationWidgetSetupLogic);
    private const short IMPORT_STATUS_IDLE = -1;

    [ExportMethod]
    public void Setup()
    {
        var systemNodePointer = Owner.Get<NodePointer>("SystemNode");
        if (systemNodePointer == null)
        {
            Log.Error(LOG_CATEGORY, "SystemNode property not defined.");
            return;
        }

        if (Owner.Context.GetNode(systemNodePointer.Value) is not FTOptix.System.System systemNode)
        {
            Log.Error(LOG_CATEGORY, "SystemNode is not defined.");
            return;
        }

        Log.Info(LOG_CATEGORY, "Creating Load Application widget variable and Load Application status event command.");

        var eventArguments = InformationModel.MakeObject("EventArguments", FTOptix.System.ObjectTypes.LoadApplicationStatusEvent);

        if (systemNode.Get("LoadApplicationStatusEventHandler") is not EventHandler statusEventHandlerNode)
        {
            statusEventHandlerNode = InformationModel.MakeObject<EventHandler>("LoadApplicationStatusEventHandler");
            systemNode.Add(statusEventHandlerNode);
            statusEventHandlerNode.ListenEventType = FTOptix.System.ObjectTypes.LoadApplicationStatusEvent;
            statusEventHandlerNode.Add(eventArguments);
        }

        var statusVariable = systemNode.GetVariable("LoadApplicationStatus");
        if (statusVariable == null)
        {
            statusVariable = InformationModel.MakeVariable("LoadApplicationStatus", OpcUa.DataTypes.Int16);
            systemNode.Add(statusVariable);
            statusVariable.SetValueNoPermissions(IMPORT_STATUS_IDLE);
        }

        SetupEventHandler(statusEventHandlerNode, statusVariable, eventArguments.GetVariable("Status"));
    }

    private static void SetupEventHandler(EventHandler eventHandlerNode,
                                          IUAVariable variableNode,
                                          IUAVariable eventArgumentVariable)
    {
        string methodContainerName = "MethodContainer" + variableNode.BrowseName;
        IUANode methodContainerNode = eventHandlerNode.Find(methodContainerName);
        if (methodContainerNode != null)
            return;

        var methodContainer = InformationModel.MakeObject(methodContainerName);

        var objectPointerVariable = InformationModel.MakeVariable<NodePointer>("ObjectPointer", OpcUa.DataTypes.NodeId);
        objectPointerVariable.Value = InformationModel.GetObject(FTOptix.CoreBase.Objects.VariableCommands).NodeId;
        methodContainer.Add(objectPointerVariable);

        var methodNameVariable = InformationModel.MakeVariable("Method", OpcUa.DataTypes.String);
        methodNameVariable.Value = "Set";
        methodContainer.Add(methodNameVariable);

        var inputArguments = InformationModel.MakeObject("InputArguments");

        var variableToModify = InformationModel.MakeVariable("VariableToModify", FTOptix.Core.DataTypes.VariablePointer);
        variableToModify.Value = variableNode.NodeId;
        inputArguments.Add(variableToModify);

        var valueVariable = InformationModel.MakeVariable("Value", OpcUa.DataTypes.Int16);
        if (eventArgumentVariable != null)
            valueVariable.SetDynamicLink(eventArgumentVariable, DynamicLinkMode.ReadWrite);
        inputArguments.Add(valueVariable);

        var arrayIndexVariable = InformationModel.MakeVariable("ArrayIndex", OpcUa.DataTypes.UInt32);
        arrayIndexVariable.ValueRank = ValueRank.Scalar;
        inputArguments.Add(arrayIndexVariable);

        methodContainer.Add(inputArguments);
        eventHandlerNode.MethodsToCall.Add(methodContainer);
    }
}
