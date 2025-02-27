using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileManager.Core.Models
{
    public class QuotaWarningEventArgs : EventArgs
    {
        public required long CurrentUsage { get; init; }
        public required long QuotaLimit { get; init; }
        public required float UsagePercentage { get; init; }
    }

}
