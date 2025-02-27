using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileManager.Core.Models
{
    public class QuotaConfiguration
    {
        public required long MaxSizeBytes { get; init; }
        public required string RootPath { get; init; }
        public float WarningThreshold { get; init; } = 0.9f; // Warn at 90% usage
        public bool EnforceQuota { get; init; } = true;
    }
}
