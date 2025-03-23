using Android.Content;
using Android.Telephony;
using Android.Provider;
using Android.OS;
using Java.Lang;
using System.Threading;
using Microsoft.Maui.ApplicationModel;
using static Android.Telephony.TelephonyCallback;
using Exception = System.Exception;
using Math = System.Math;

namespace FileManagerP2P.Platforms.Android.Services
{
    public class AndroidTelephonyService : ITelephonyService, IDisposable
    {
        private readonly TelephonyManager? _telephonyManager;
        private SignalStrengthListener? _signalStrengthListener;
        private MyTelephonyCallback? _telephonyCallback;
        private int _currentSignalStrength = -1;
        private readonly Handler? _mainHandler;

        public AndroidTelephonyService()
        {
            _telephonyManager = Platform.CurrentActivity?.GetSystemService(Context.TelephonyService) as TelephonyManager;
            _mainHandler = new Handler(Looper.MainLooper!);
            InitializeSignalStrengthListener();
        }

        private void InitializeSignalStrengthListener()
        {
            if (_telephonyManager == null)
                return;

            try
            {
                // For Android 12 (API 31) and above, use TelephonyCallback
                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    _telephonyCallback = new MyTelephonyCallback(this);
                    _telephonyManager.RegisterTelephonyCallback(
                        Platform.CurrentActivity!.MainExecutor!,
                        _telephonyCallback);
                }
                // For older Android versions, use PhoneStateListener
                else
                {
                    _signalStrengthListener = new SignalStrengthListener(this);
                    _telephonyManager.Listen(_signalStrengthListener, PhoneStateListenerFlags.SignalStrengths);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize signal strength listener: {ex.Message}");
            }
        }

        public string GetDeviceId()
        {
            // Existing implementation
            try
            {
                string deviceId;
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    // For Android 8.0 (API 26) and above, use the Settings.Secure.AndroidId
                    deviceId = Settings.Secure.GetString(
                        Platform.CurrentActivity?.ContentResolver,
                        Settings.Secure.AndroidId) ?? "";
                }
                else
                {
                    // For older Android versions
                    deviceId = _telephonyManager?.DeviceId ?? "";
                }

                return !string.IsNullOrEmpty(deviceId) ? deviceId : Guid.NewGuid().ToString();
            }
            catch
            {
                return Guid.NewGuid().ToString();
            }
        }

        // Other existing implementation methods...
        public bool HasPhoneCapabilities() => _telephonyManager?.PhoneType != PhoneType.None;
        public string GetNetworkOperatorName() => _telephonyManager?.NetworkOperatorName ?? "Unknown";
        public int GetSignalStrength() => _currentSignalStrength;
        public string GetDeviceModel() => Build.Model ?? "Unknown Device";
        public bool IsNetworkRoaming() => _telephonyManager?.IsNetworkRoaming ?? false;

        // Method to update the signal strength value
        internal void UpdateSignalStrength(int signalStrength)
        {
            _currentSignalStrength = signalStrength;
        }

        // Clean up resources when the service is no longer needed
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            if (_telephonyManager != null)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(31) && _telephonyCallback != null)
                {
                    _telephonyManager.UnregisterTelephonyCallback(_telephonyCallback);
                }
                else if (_signalStrengthListener != null)
                {
                    _telephonyManager.Listen(_signalStrengthListener, PhoneStateListenerFlags.None);
                }
            }
        }

        // Inner class for Android 12+ using TelephonyCallback
        private class MyTelephonyCallback(AndroidTelephonyService service) : TelephonyCallback, ISignalStrengthsListener
        {
            private readonly AndroidTelephonyService _service = service;


            public void OnSignalStrengthsChanged(SignalStrength signalStrength)
            {
                ProcessSignalStrength(signalStrength);
            }

            private void ProcessSignalStrength(SignalStrength signalStrength)
            {
                try
                {
                    int strength = -1;

                    if (signalStrength.CellSignalStrengths != null && signalStrength.CellSignalStrengths.Count > 0)
                    {
                        // Get the first available cell signal strength
                        var cellSignal = signalStrength.CellSignalStrengths[0];

                        int asu = cellSignal.AsuLevel;
                        if (asu > 0)
                        {
                            // Calculate a percentage based on ASU level
                            int maxAsu = cellSignal is CellSignalStrengthLte ? 97 : 31;
                            strength = (int)Math.Min(100, Math.Round((double)asu / maxAsu * 100));
                        }
                        else
                        {
                            // Fall back to signal level if ASU is not available
                            strength = cellSignal.Level * 25; // Level is typically 0-4, multiply by 25 to get 0-100
                        }
                    }
                    else
                    {
                        // Fall back to simple level approach
                        strength = signalStrength.Level * 25;
                    }

                    // Update the signal strength in the service
                    _service.UpdateSignalStrength(strength);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error getting signal strength: {ex.Message}");
                    _service.UpdateSignalStrength(-1);
                }
            }
        }

        // Inner class for Android 11 and below using PhoneStateListener
        private class SignalStrengthListener(AndroidTelephonyService service) : PhoneStateListener
        {
            private readonly AndroidTelephonyService _service = service;

            public override void OnSignalStrengthsChanged(SignalStrength signalStrength)
            {
                base.OnSignalStrengthsChanged(signalStrength);

                try
                {
                    int strength = -1;

                    // For Android 10 (API 29) and above
                    if (OperatingSystem.IsAndroidVersionAtLeast(29))
                    {
                        if (signalStrength.CellSignalStrengths != null && signalStrength.CellSignalStrengths.Count > 0)
                        {
                            // Get the first available cell signal strength
                            var cellSignal = signalStrength.CellSignalStrengths[0];

                            int asu = cellSignal.AsuLevel;
                            if (asu > 0)
                            {
                                // Calculate a percentage based on ASU level
                                int maxAsu = cellSignal is CellSignalStrengthLte ? 97 : 31;
                                strength = (int)Math.Min(100, Math.Round((double)asu / maxAsu * 100));
                            }
                            else
                            {
                                // Fall back to signal level if ASU is not available
                                strength = cellSignal.Level * 25; // Level is typically 0-4, multiply by 25 to get 0-100
                            }
                        }
                    }
                    else if (OperatingSystem.IsAndroidVersionAtLeast(26))
                    {
                        // Use signalStrength.Level which is available on API 26+
                        strength = signalStrength.Level * 25; // Level is typically 0-4, multiply by 25 to get 0-100
                    }
                    else
                    {
                        // For older Android versions
                        // Use reflection to access the GsmSignalStrength method if available
                        try
                        {
                            var javaClass = signalStrength.Class;
                            var method = javaClass.GetMethod("getGsmSignalStrength");
                            if (method != null)
                            {
                                int gsmSignalStrength = (int)method.Invoke(signalStrength, null);
                                if (gsmSignalStrength != 99) // 99 is unknown
                                {
                                    // GSM signal strength is 0-31, convert to percentage
                                    strength = (int)Math.Min(100, Math.Round((double)gsmSignalStrength / 31 * 100));
                                }
                            }
                            else
                            {
                                // Fall back to simple level approach if getGsmSignalStrength not available
                                strength = signalStrength.Level * 25;
                            }
                        }
                        catch
                        {
                            // If reflection fails, try the level property
                            strength = signalStrength.Level * 25;
                        }
                    }

                    // Update the signal strength in the service
                    _service.UpdateSignalStrength(strength);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error getting signal strength: {ex.Message}");
                    _service.UpdateSignalStrength(-1);
                }
            }
        }
    }
}
