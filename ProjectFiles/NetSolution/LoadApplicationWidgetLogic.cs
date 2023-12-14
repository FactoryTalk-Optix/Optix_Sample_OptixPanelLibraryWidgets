#region Using directives
using System.Timers;
using UAManagedCore;
using FTOptix.UI;
using FTOptix.System;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.DataLogger;
using FTOptix.Store;
using FTOptix.WebUI;
#endregion

public class LoadApplicationWidgetLogic : BaseNetLogic
{
    private const string LOG_CATEGORY = nameof(LoadApplicationWidgetLogic);
    private const int STATUS_MESSAGE_DISPLAY_SECONDS = 5;

    private const short IMPORT_STATUS_IDLE = -1;
    private const short IMPORT_STATUS_IN_PROGRESS = -2;

    public override void Start()
    {
        var systemNodePointer = Owner.Get<NodePointer>("SystemNode");
        if (systemNodePointer == null)
        {
            Log.Error(LOG_CATEGORY, "SystemNode property not defined.");
            return;
        }

        systemNode = Owner.Context.GetNode(systemNodePointer.Value) as FTOptix.System.System;
        if (systemNode == null)
        {
            Log.Error(LOG_CATEGORY, "SystemNode is not defined.");
            return;
        }

        statusVariable = systemNode.Get("LoadApplicationStatus") as UAVariable;
        if (statusVariable == null)
        {
            Log.Error("LoadApplicationStatus variable does not exist. Did you remember to run the Setup function in the Designer?");
            return;
        }

        // setup our own event handler to enable the timer
        systemNode.OnLoadApplicationStatusEvent += HandleLoadApplicationStatus;

        statusVariableTimer = new Timer(STATUS_MESSAGE_DISPLAY_SECONDS * 1000);
        statusVariableTimer.Elapsed += ResetStatusVariableOnTimer;

        if (systemNode.Device.ProtectionModeEnabled)
            statusVariable.SetValueNoPermissions((short)LoadApplicationStatus.ProtectionModeActive);

        if (statusVariable.Value != IMPORT_STATUS_IDLE &&
            statusVariable.Value != IMPORT_STATUS_IN_PROGRESS &&
            !systemNode.Device.ProtectionModeEnabled)
        {
            // the error has already been set and we are returning to the widget
            // need to start the timer now so that the error message will still disappear
            statusVariableTimer?.Start();
        }
    }

    public override void Stop()
    {
        if (systemNode != null)
            systemNode.OnLoadApplicationStatusEvent -= HandleLoadApplicationStatus;

        if (statusVariableTimer != null)
            statusVariableTimer.Elapsed -= ResetStatusVariableOnTimer;

        // destruction cleanup
        systemNode = null;
        statusVariable = null;
        statusVariableTimer = null;
    }

    [ExportMethod]
    public void LoadApplication(string filePath, string password, bool deleteApplicationFiles)
    {
        if (systemNode == null)
        {
            Log.Error(LOG_CATEGORY, "SystemNode reference not defined. Load application failed.");
            return;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            Log.Error(LOG_CATEGORY, "File path is empty. Load application failed.");
            return;
        }

        // make sure the timer is stopped, otherwise it can reset while the import is in-progress and clear the status message
        statusVariableTimer?.Stop();

        if (deleteApplicationFiles)
        {
            if (Owner.Find("ConfirmResetFilesContext") is not ConfirmationDialogContext confirmationDialogContext)
            {
                Log.Error("ConfirmationDialogContext is not defined. Load application failed.");
                return;
            }

            if (confirmationDialogContext.GetAlias("ConfirmationDialogAlias") is not DialogType confirmationDialog)
            {
                Log.Error("ConfirmationDialog not found. Load application failed.");
                return;
            }

            ItemExtensions.OpenDialog(Owner as Screen, confirmationDialog, confirmationDialogContext.NodeId);
        }
        else
            LoadApplicationInternal(filePath, password, deleteApplicationFiles);
    }

    [ExportMethod]
    public void ConfirmLoadApplication(string filePath, string password, bool deleteApplicationFiles)
    {
        LoadApplicationInternal(filePath, password, deleteApplicationFiles);
    }

    private void LoadApplicationInternal(string filePath, string password, bool deleteApplicationFiles)
    {
        statusVariable?.SetValueNoPermissions(IMPORT_STATUS_IN_PROGRESS);
        try
        {
            if (!systemNode.LoadApplication(filePath, password, deleteApplicationFiles))
                HandleLoadApplicationStatusInternal((short)LoadApplicationStatus.InternalError);
        }
        catch
        {
            HandleLoadApplicationStatusInternal((short)LoadApplicationStatus.InternalError);
        }
    }

    private void HandleLoadApplicationStatus(object sender, LoadApplicationStatusEvent args)
    {
        statusVariableTimer?.Start();
    }

    private void HandleLoadApplicationStatusInternal(short statusCode)
    {
        statusVariable?.SetValueNoPermissions(statusCode);
        statusVariableTimer?.Start();
    }

    private void ResetStatusVariableOnTimer(object sender, ElapsedEventArgs args)
    {
        statusVariableTimer?.Stop();
        statusVariable?.SetValueNoPermissions(IMPORT_STATUS_IDLE);
    }

    private FTOptix.System.System systemNode;
    private UAVariable statusVariable;
    private Timer statusVariableTimer;
}
