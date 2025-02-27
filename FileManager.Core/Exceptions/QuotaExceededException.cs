using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileManager.Core.Exceptions
{
    public class QuotaExceededException : Exception
    {
        public QuotaExceededException(long requested, long available)
            : base($"Quota exceeded. Requested: {requested}, Available: {available}")
        {
            RequestedBytes = requested;
            AvailableBytes = available;
        }

        public long RequestedBytes { get; }
        public long AvailableBytes { get; }
    }

}
