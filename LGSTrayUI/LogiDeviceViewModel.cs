using CommunityToolkit.Mvvm.ComponentModel;
using Hardcodet.Wpf.TaskbarNotification;
using LGSTrayCore;
using LGSTrayPrimitives.MessageStructs;
using System;

namespace LGSTrayUI
{
    public class LogiDeviceViewModelFactory
    {
        private readonly LogiDeviceIconFactory _logiDeviceIconFactory;
        private readonly UserSettingsWrapper _userSettings;

        public LogiDeviceViewModelFactory(LogiDeviceIconFactory logiDeviceIconFactory, UserSettingsWrapper userSettings)
        {
            _logiDeviceIconFactory = logiDeviceIconFactory;
            _userSettings = userSettings;
        }

        public LogiDeviceViewModel CreateViewModel(Action<LogiDeviceViewModel>? config = null)
        {
            LogiDeviceViewModel output = new(_logiDeviceIconFactory, _userSettings);
            config?.Invoke(output);

            return output;
        }
    }

    public partial class LogiDeviceViewModel : LogiDevice
    {
        private readonly LogiDeviceIconFactory _logiDeviceIconFactory;

        [ObservableProperty]
        private bool _isChecked = false;

        private LogiDeviceIcon? taskbarIcon;

        private readonly UserSettingsWrapper _userSettings;

        public LogiDeviceViewModel(LogiDeviceIconFactory logiDeviceIconFactory, UserSettingsWrapper userSettings)
        {
            _logiDeviceIconFactory = logiDeviceIconFactory;
            _userSettings = userSettings;

            _userSettings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserSettingsWrapper.TaskbarWidget))
                {
                    UpdateTrayIcon();
                }
            };
        }

        partial void OnIsCheckedChanged(bool oldValue, bool newValue) => UpdateTrayIcon();

        /// <summary>
        /// A selected device gets its own tray icon, unless the taskbar widget is on:
        /// the widget already shows the battery, so the tray icons would just be duplicates.
        /// </summary>
        private void UpdateTrayIcon()
        {
            if (IsChecked && !_userSettings.TaskbarWidget)
            {
                taskbarIcon ??= _logiDeviceIconFactory.CreateDeviceIcon(this);
            }
            else
            {
                taskbarIcon?.Dispose();
                taskbarIcon = null;
            }
        }

        public void UpdateState(InitMessage initMessage)
        {
            if (string.IsNullOrEmpty(DeviceId) || DeviceId == NOT_FOUND)
            {
                DeviceId = initMessage.deviceId;
            }

            DeviceName = initMessage.deviceName;
            HasBattery = initMessage.hasBattery;
            DeviceType = initMessage.deviceType;
        }

        public void UpdateState(UpdateMessage updateMessage)
        {
            BatteryPercentage = updateMessage.batteryPercentage;
            PowerSupplyStatus = updateMessage.powerSupplyStatus;
            BatteryVoltage = updateMessage.batteryMVolt / 1000.0;
            BatteryMileage = updateMessage.Mileage;
            LastUpdate = updateMessage.updateTime;
        }
    }
}
