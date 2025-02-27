using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileManager.Core.Models
{
    public record QuotaInfo(
     long CurrentUsageBytes,
     long MaxQuotaBytes,
     float UsagePercentage,
     bool IsExceeded
 );

}
