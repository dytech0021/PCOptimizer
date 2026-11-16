using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PCOptimizer.Services
{
    /// <summary>
    /// "PC ocioso" como no Wise: CPU abaixo de 10% em todas as amostras dos
    /// últimos 5 minutos. Recebe os contadores crus de GetSystemTimes.
    /// Não é thread-safe — é usado só dentro do tick do automático.
    /// </summary>
    public sealed class CpuIdleTracker
    {
        private readonly int _idlePercent;
        private readonly TimeSpan _required;
        private long _idle, _kernel, _user;
        private DateTime? _lastSampleUtc;
        private DateTime? _idleSinceUtc;

        public CpuIdleTracker(int idlePercent, TimeSpan required)
        {
            _idlePercent = idlePercent;
            _required = required;
        }

        public double? LastBusyPercent { get; private set; }

        /// <summary>Contadores de GetSystemTimes; o tempo de kernel INCLUI o ocioso.</summary>
        public void Push(long idle, long kernel, long user, DateTime nowUtc)
        {
            if (_lastSampleUtc is DateTime prevUtc)
            {
                long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
                long total = dk + du;
                if (total > 0 && di >= 0 && di <= total)
                {
                    double busy = 100.0 * (total - di) / total;
                    LastBusyPercent = busy;
                    if (busy < _idlePercent) _idleSinceUtc ??= prevUtc;
                    else _idleSinceUtc = null;
                }
            }
            _idle = idle; _kernel = kernel; _user = user;
            _lastSampleUtc = nowUtc;
        }

        public void Reset()
        {
            _lastSampleUtc = null;
            _idleSinceUtc = null;
            LastBusyPercent = null;
        }

        public bool IsIdle(DateTime nowUtc) =>
            _idleSinceUtc is DateTime since && nowUtc - since >= _required;
    }
}
