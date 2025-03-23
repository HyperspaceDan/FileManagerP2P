using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileManagerP2P.Services
{
    public interface ITelephonyService
    {
        string GetDeviceId();
        bool HasPhoneCapabilities();
        string GetNetworkOperatorName();
        int GetSignalStrength();
        string GetDeviceModel();
        bool IsNetworkRoaming();
    }
}
