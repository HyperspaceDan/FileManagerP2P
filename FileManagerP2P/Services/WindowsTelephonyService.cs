using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileManagerP2P.Services
{
    public class WindowsTelephonyService : ITelephonyService
    {
        public string GetDeviceId()
        {
            // Windows implementation - return a computer name or other unique identifier
            return Environment.MachineName;
        }

        public bool HasPhoneCapabilities() => false;
        public string GetNetworkOperatorName() => "Windows Network";
        public int GetSignalStrength() => 100;
        public string GetDeviceModel() => Environment.OSVersion.ToString();
        public bool IsNetworkRoaming() => false;
    }
}
