using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FileManager.Core.Models;
using FileManager.Core.Exceptions;

namespace FileManager.Core.Interfaces
{
    public interface IQuotaManager
    {
        Task<QuotaInfo> GetQuotaInfo(CancellationToken cancellationToken = default);
        Task ValidateQuota(long requiredBytes, CancellationToken cancellationToken = default);
        event EventHandler<QuotaWarningEventArgs>? QuotaWarningRaised;
    }
}
