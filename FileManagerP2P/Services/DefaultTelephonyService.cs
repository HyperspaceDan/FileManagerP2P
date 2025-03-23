using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileManagerP2P.Services
{
    public class DefaultTelephonyService : ITelephonyService
    {
        public string GetDeviceId() => Guid.NewGuid().ToString();
        public bool HasPhoneCapabilities() => false;
        public string GetNetworkOperatorName() => "Unknown";
        public int GetSignalStrength() => -1;
        public string GetDeviceModel() => "Unknown Device";
        public bool IsNetworkRoaming() => false;
    }
}
