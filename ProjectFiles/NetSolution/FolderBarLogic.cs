#region Using directives
using System;
using UAManagedCore;
using FTOptix.NetLogic;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.Core;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using FilesystemBrowserHelper;
using FTOptix.DataLogger;
using FTOptix.Store;
using FTOptix.WebUI;
#endregion

class Location
{
	public Location(
		string locationVariableBrowseName,
		string locationVariableValue,
		string locationVariableDisplayName,
		string locationVariableDisplayNameValue)
    {
		LocationVariableBrowseName = locationVariableBrowseName;
		LocationVariableValue = locationVariableValue;
		LocationVariableDisplayName = locationVariableDisplayName;
		LocationVariableDisplayNameValue = locationVariableDisplayNameValue;
	}

    public string LocationVariableBrowseName { get; }
    public string LocationVariableValue { get; }
	public string LocationVariableDisplayName { get; }
	public string LocationVariableDisplayNameValue { get; }
}

class USBLocation : Location
{
	public USBLocation(
		string locationVariableBrowseName,
		string locationVariableValue,
		string locationVariableDisplayName,
		string locationVariableDisplayNameValue)
		: base(
			locationVariableBrowseName,
			locationVariableValue,
			locationVariableDisplayName,
			locationVariableDisplayNameValue)
	{
	}
}

class SystemLocation : Location
{
	public SystemLocation(
		string locationVariableBrowseName,
		string locationVariableValue,
		string locationVariableDisplayName,
		string locationVariableDisplayNameValue)
		: base(
			locationVariableBrowseName,
			locationVariableValue,
			locationVariableDisplayName,
			locationVariableDisplayNameValue)
	{
	}
}

public class FolderBarLogic : BaseNetLogic
{
	public override void Start()
	{
		isFreeNavigationSupportedForCurrentPlatform = PlatformConfigurationHelper.IsFreeNavigationSupported();

		// Path variables
		pathVariable = LogicObject.GetVariable("Path");
		if (pathVariable == null)
			throw new CoreConfigurationException("Path variable not found in FilesystemBrowserLogic");

		// FolderBar variables
		locationsComboBox = Owner.Get<ComboBox>("Locations");
		if (locationsComboBox == null)
			throw new CoreConfigurationException("Locations combo box not found");

		relativePathTextBox = Owner.Get<TextBox>("RelativePath");
		if (relativePathTextBox == null)
			throw new CoreConfigurationException("RelativePath textbox not found");

		accessFullFilesystemVariable = Owner.Owner.GetVariable("AccessFullFilesystem");
		if (accessFullFilesystemVariable == null)
			throw new CoreConfigurationException("AccessFullFilesystem variable not found");

		if (accessFullFilesystemVariable.Value && !isFreeNavigationSupportedForCurrentPlatform)
			return;

		accessNetworkDrivesVariable = Owner.Owner.GetVariable("AccessNetworkDrives");
		if (accessNetworkDrivesVariable == null)
			throw new CoreConfigurationException("AccessNetworkDrives variable not found");

		// In case of invalid path the fall back is %PROJECTDIR%
		var startFolderPathResourceUri = new ResourceUri(pathVariable.Value);

		resourceUriHelper = new ResourceUriHelper(LogicObject.NodeId.NamespaceIndex);
		if (!resourceUriHelper.IsFolderPathAllowed(startFolderPathResourceUri, accessFullFilesystemVariable.Value, accessNetworkDrivesVariable.Value))
		{
			startFolderPathResourceUri = resourceUriHelper.GetDefaultResourceUri();
			pathVariable.Value = startFolderPathResourceUri;
		}

		locationsObject = Owner.Owner.GetObject("Locations");
		if (locationsObject == null)
			throw new CoreConfigurationException("Locations object not found");

		InitalizeLocationsObject();
		resourceUriHelper.LocationsObject = locationsObject;

		InitializeComboBoxAndTextBox(startFolderPathResourceUri);

		pathVariable.VariableChange += PathVariable_VariableChange;
		locationsComboBox.SelectedValueVariable.VariableChange += SelectedValueComboBox_VariableChange;
		relativePathTextBox.OnUserTextChanged += RelativePathTextBox_UserTextChanged;

        periodicTaskDeviceUpdater = new PeriodicTask(UpdateDevices, deviceUpdaterPeriod, LogicObject);
        periodicTaskDeviceUpdater.Start();
    }

	public override void Stop()
	{
		pathVariable.VariableChange -= PathVariable_VariableChange;
		locationsComboBox.SelectedValueVariable.VariableChange -= SelectedValueComboBox_VariableChange;
		relativePathTextBox.OnUserTextChanged -= RelativePathTextBox_UserTextChanged;
		periodicTaskDeviceUpdater.Cancel();
	}

	private void SelectedValueComboBox_VariableChange(object sender, VariableChangeEventArgs e)
	{
		// Clear RelativePath Textbox and update the current path (a new browse is made)
		SetTextBoxValue(string.Empty);
		pathVariable.Value = new ResourceUri(e.NewValue);
	}

	private void RelativePathTextBox_UserTextChanged(object sender, UserTextChangedEvent e)
	{
		var updatedRelativePathString = e.NewText.Text;

		if (updatedRelativePathString.Contains(".."))
		{
			Log.Error("FolderBarLogic", $"Input is incorrect: '..' is not supported");
			SetTextBoxValue(lastTexboxValidText);
			return;
		}

		if (Path.IsPathRooted(updatedRelativePathString))
		{
			Log.Error("FolderBarLogic", "Input is incorrect: cannot insert a full path");
			SetTextBoxValue(lastTexboxValidText);
			return;
		}

		// Update pathVariable value with the text inserted into the textbox
		string ftoptixstudioBasePath = (string)locationsComboBox.SelectedValue;

		bool endsWithSlash = ftoptixstudioBasePath.EndsWith("/") || ftoptixstudioBasePath.EndsWith("\\");
		if (!endsWithSlash && !string.IsNullOrEmpty(updatedRelativePathString))
			ftoptixstudioBasePath += "/";

		string updatedPathResourceUriString = ftoptixstudioBasePath + updatedRelativePathString;
		ResourceUri updatedResourceUri = new ResourceUri(updatedPathResourceUriString);
		if (!resourceUriHelper.IsResourceUriValid(updatedResourceUri))
		{
			Log.Error("FolderBarLogic", $"Input is incorrect: folder path {updatedPathResourceUriString} does not exist");
			SetTextBoxValue(lastTexboxValidText);
			return;
		}

		pathVariable.Value = ftoptixstudioBasePath + updatedRelativePathString;
	}

	private void PathVariable_VariableChange(object sender, VariableChangeEventArgs e)
	{
		var updatedPathResourceUri = new ResourceUri(e.NewValue);

		// Update the textbox with the relative path to the current selected location
		try
		{
			SetTextBoxValue(resourceUriHelper.GetRelativePathToLocationFromResourceUri(updatedPathResourceUri));
		}
		catch (Exception exception)
		{
			Log.Error("FolderBarLogic", $"Unable to set value on textbox. Path '{e.NewValue.Value}' not found: {exception.Message}");
			return;
		}
	}

	private void InitalizeLocationsObject()
	{
		// Set the namespace prefix to %APPLICATIONDIR% and %PROJECTDIR%
		AddNamespacePrefixToStandardLocations();

		// Detect connected USB devices
		InitializeUSBDevices();

		// Detect fixed drives
		if (accessFullFilesystemVariable.Value)
		{
			if (Environment.OSVersion.Platform != PlatformID.Unix)
				InitializeSystemDrives();
		}

		AddLocations();
	}

	private void AddNamespacePrefixToStandardLocations()
	{
		foreach (IUAVariable location in locationsObject.Children)
			location.Value = resourceUriHelper.AddNamespacePrefixToFTOptixRuntimeFolder((string)location.Value.Value);
	}

	private void InitializeSystemDrives()
	{
		if (!accessFullFilesystemVariable.Value)
			return;

		DriveInfo[] drives;
		try
		{
			drives = DriveInfo.GetDrives();
		}
		catch (Exception exception)
		{
			Log.Error("FolderBarLogic", $"Unable to get the list of system drives: {exception.Message}");
			return;
		}

		currentlyConnectedSystemDrives = new List<string>();
		foreach (var drive in drives)
		{
			// Skip USB hard drive detected with USBUri API
			if (detectedUSBDrives.Contains(drive.Name))
				continue;

			string driveName = drive.RootDirectory.FullName;
			var systemDriveNode = locationsObject.GetVariable(driveName);
			bool systemdriveNodeAlreadyExists = systemDriveNode != null;

			bool isFixedDrive = drive.DriveType == DriveType.Fixed;
			bool isNetworkDrive = drive.DriveType == DriveType.Network;

			// A network drive is valid only if AccessFullFilesystem and AccessNetworkDrives are true
			if (isNetworkDrive && !accessNetworkDrivesVariable.Value)
				continue;

			// Check if network drives are reachable
			if (isNetworkDrive && !PlatformConfigurationHelper.IsWindowsNetworkDriveAccessible(driveName))
				continue;

			// Drive is reachable
			if (isFixedDrive || isNetworkDrive)
			{
				// Assumption: in the small periodic task update period (e.g. 5secs) there is collision of system drive names.
				// For example in that period we assume that this case is not possible:
				// D:/ is removed and then a different disk is inserted with the same drive name D:/
				currentlyConnectedSystemDrives.Add(driveName);
				if (systemdriveNodeAlreadyExists)
					continue;

				locations.Add(new SystemLocation(driveName,
					ResourceUri.FromAbsoluteFilePath(driveName),
					$"WindowsDrive_{driveName}",
					driveName));
			}
		}
	}

	private void UpdateSystemDrives()
	{
		if (!accessFullFilesystemVariable.Value)
			return;

		// If a disk (external hard drive or network drive) is removed, it is not retrieved by DriveInfo.GetDrives()
		var initialConnectedSystemDrives = currentlyConnectedSystemDrives;
		InitializeSystemDrives();

		if (initialConnectedSystemDrives.Count == currentlyConnectedSystemDrives.Count)
			return;

		if (initialConnectedSystemDrives.Count < currentlyConnectedSystemDrives.Count)
		{
			var connectedDrives = currentlyConnectedSystemDrives.Except(initialConnectedSystemDrives).ToList();
			foreach (var drive in connectedDrives)
				Log.Info("FolderBarLogic", $"Drive {drive} has been connected");
		}

		// Check if a system drive in locations is no longer attached
		if (initialConnectedSystemDrives.Count > currentlyConnectedSystemDrives.Count)
		{
			var disconnectedDrives = initialConnectedSystemDrives.Except(currentlyConnectedSystemDrives).ToList();
			foreach (var drive in disconnectedDrives)
			{
				var driveNode = locationsObject.Find(drive);
				if (driveNode != null)
				{
					Log.Info("FolderBarLogic", $"Drive {drive} has been disconnected");

					// Fall back to %PROJECTDIR% if the currently selected drive is disconnected
					ResourceUri selectedComboBoxValue = new ResourceUri((string)locationsComboBox.SelectedValue);
					var currentlySelectedDriveRoot = PlatformConfigurationHelper.GetWindowsDrivePathRoot(selectedComboBoxValue);
					if (currentlySelectedDriveRoot == drive)
						locationsComboBox.SelectedValue = resourceUriHelper.GetDefaultResourceUriAsString();

					locationsObject.Remove(driveNode);
				}
			}
		}
	}

	private void InitializeLinuxRootFolder()
	{
		// Non supported platforms was already filtered out
		// Only Debian is supported
		string linuxRootBrowseName = PlatformConfigurationHelper.GetGenericLinuxBaseBrowseNameFolder();
		string linuxRootResourceUri = ResourceUri.FromAbsoluteFilePath(PlatformConfigurationHelper.GetGenericLinuxBaseFolderPath());

		locations.Add(new SystemLocation(linuxRootBrowseName,
			linuxRootResourceUri,
			"LinuxRoot",
			linuxRootBrowseName));
	}

	private void InitializeUSBDevices()
	{
		uint connectedUsbDevices = 0;
		detectedUSBDrives.Clear();

		for (uint i = 1; i <= maxConnectedUsbDevices; ++i)
		{
			var usbLocationBrowseName = $"USB{i}";
			var usbResourceUri = new ResourceUri($"%{usbLocationBrowseName}%");
			var usbDeviceNode = locationsObject.GetVariable(usbLocationBrowseName);
			if (!resourceUriHelper.IsResourceUriValid(usbResourceUri))
				break;

			connectedUsbDevices++;

			if (Environment.OSVersion.Platform != PlatformID.Unix)
				detectedUSBDrives.Add(Path.GetPathRoot(usbResourceUri.Uri));

			// USB<i> location is valid and it already exists
			if (usbDeviceNode != null)
				continue;

			string usbName;
			if (Environment.OSVersion.Platform != PlatformID.Unix)
				usbName = $"USB {i} ({Path.GetPathRoot(usbResourceUri.Uri)})";
			else
				usbName = $"USB {i}";

			locations.Add(new USBLocation($"USB{i}",
				usbResourceUri,
				"ComboBoxFileSelectorUSBDisplayName",
				usbName));
		}

		currentlyConnectedUsbDevices = connectedUsbDevices;
	}

	private void UpdateDevices()
	{
		uint initialConnectedUsbDevices = currentlyConnectedUsbDevices;
		InitializeUSBDevices();

		if (Environment.OSVersion.Platform == PlatformID.Win32NT)
			UpdateSystemDrives();

		AddLocations();

		if (currentlyConnectedUsbDevices == initialConnectedUsbDevices)
			return;

		bool usbConnected = currentlyConnectedUsbDevices > initialConnectedUsbDevices;
		string usbStatus = (usbConnected) ? "connected" : "disconnected";
		Log.Info("FolderBarLogic", $"USB mass storage device has been {usbStatus}");

		// If a USB stick device is currently selected and USB devices changes it is now invalid.
		// 1. Disconnection of a USB
		// E.g. USB1 and USB2 are detected at start. Then USB1 is disconnected. USB2 is promoted by FTOptixStudio as USB1:
		// if we are browsing USB1 the current view and path values are outdated (because USB1 is a different device now);
		// if we are browsing USB2 the current view and path values are outdated (because USB2 does not exist anymore)
		// 2. Connection of a USB
		// E.g. USB1 is connected at start. USB2 is connected later. It is not guaranteed that the enumeration preserves the order.
		// It depends on the physical port: if USB2 physical port is enumerated before USB1 physical port, then values are invalid.
		// We must fall back to %PROJECTDIR%
		ResourceUri selectedComboBoxValue = new ResourceUri((string)locationsComboBox.SelectedValue);
		if (selectedComboBoxValue.UriType == UriType.USBRelative)
			locationsComboBox.SelectedValue = resourceUriHelper.GetDefaultResourceUriAsString();

		// Remove invalid (e.g. disconnected) USB devices
		for (uint i = 1; i <= maxConnectedUsbDevices; ++i)
		{
			var usbLocationBrowseName = $"USB{i}";
			var usbDeviceNode = locationsObject.GetVariable(usbLocationBrowseName);
			if (usbDeviceNode != null)
			{
				var usbResourceUri = new ResourceUri($"%{usbLocationBrowseName}%");
				// USB<i> location still exists but it is invalid
				if (!resourceUriHelper.IsResourceUriValid(usbResourceUri))
					locationsObject.Remove(usbDeviceNode);
			}
		}
	}

	private void AddLocations()
	{
		var systemDrives = locations.OfType<SystemLocation>();
		foreach (var location in systemDrives)
			AddLocation(location);

		var usbDrives = locations.OfType<USBLocation>();
		foreach (var location in usbDrives)
			AddLocation(location);

		locations.Clear();
	}

	private void AddLocation(Location location)
	{
		var locationVariable = InformationModel.MakeVariable(location.LocationVariableBrowseName, FTOptix.Core.DataTypes.ResourceUri);
		locationVariable.Value = location.LocationVariableValue;

		var localeId = Session.User.LocaleId;
		if (String.IsNullOrEmpty(localeId))
			Log.Error("FolderBarLogic", "No locale found for the current user");

		locationVariable.DisplayName = new LocalizedText(location.LocationVariableDisplayName, location.LocationVariableDisplayNameValue, localeId);

		locationsObject.Add(locationVariable);
	}

	private void InitializeComboBoxAndTextBox(ResourceUri startFolderPathResourceUri)
	{
		locationsComboBox.SelectedValue = resourceUriHelper.GetBaseLocationPathFromLocationsObject(startFolderPathResourceUri);
		SetTextBoxValue(resourceUriHelper.GetRelativePathToLocationFromResourceUri(startFolderPathResourceUri));
	}

	private void SetTextBoxValue(string text)
	{
		lastTexboxValidText = text;
		relativePathTextBox.Text = lastTexboxValidText;
	}

	private IUAVariable pathVariable;

	// FolderBar variables
	private TextBox relativePathTextBox;
	private ComboBox locationsComboBox;
	private IUAObject locationsObject;

	private IUAVariable accessFullFilesystemVariable;
	private IUAVariable accessNetworkDrivesVariable;
	private bool isFreeNavigationSupportedForCurrentPlatform;

	private PeriodicTask periodicTaskDeviceUpdater;
	private readonly int deviceUpdaterPeriod = 5000;

	private readonly uint maxConnectedUsbDevices = 5;
	private uint currentlyConnectedUsbDevices = 0;
	private List<string> currentlyConnectedSystemDrives;

	private string lastTexboxValidText = "";
	private ResourceUriHelper resourceUriHelper;

	private readonly HashSet<string> detectedUSBDrives =  new HashSet<string>();
	private readonly List<Location> locations = new List<Location>();
}
