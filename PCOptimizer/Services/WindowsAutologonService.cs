using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Configura o auto-login do Windows via as chaves de registro AutoAdminLogon
    /// em HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon.
    ///
    /// Funciona em todas as versões do Windows 10 e 11 (incluindo 25H2) porque
    /// usa o mecanismo de registro de baixo nível, não o netplwiz (cuja checkbox
    /// foi removida da UI do Windows 11 para contas Microsoft, mas a chave
    /// AutoAdminLogon continua sendo honrada pelo Winlogon).
    ///
    /// Atenção: a senha fica em texto simples em HKLM (leitura exige admin).
    /// </summary>
    public static class WindowsAutologonService
    {
        private const string WinlogonPath =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LogonUser(string username, string domain, string password,
            int logonType, int logonProvider, out IntPtr token);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const int Logon32LogonInteractive = 2;
        private const int Logon32ProviderDefault  = 0;

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath);
                return (key?.GetValue("AutoAdminLogon") as string) == "1";
            }
            catch { return false; }
        }

        public static string GetConfiguredUser()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath);
                return (key?.GetValue("DefaultUserName") as string) ?? Environment.UserName;
            }
            catch { return Environment.UserName; }
        }

        /// <summary>
        /// Tenta validar as credenciais via LogonUser, tentando domínio local
        /// (nome do PC), "." e "" para cobrir contas locais e Microsoft.
        /// </summary>
        public static bool ValidateCredentials(string username, string password)
        {
            if (string.IsNullOrEmpty(password)) return true; // sem senha: deixa passar

            string[] domains = { Environment.MachineName, ".", "" };
            foreach (var domain in domains)
            {
                IntPtr token = IntPtr.Zero;
                try
                {
                    if (LogonUser(username, domain, password,
                        Logon32LogonInteractive, Logon32ProviderDefault, out token))
                    {
                        return true;
                    }
                }
                catch { }
                finally
                {
                    if (token != IntPtr.Zero) try { CloseHandle(token); } catch { }
                }
            }
            return false;
        }

        /// <summary>
        /// Ativa o auto-login. Não valida internamente — chame ValidateCredentials antes.
        /// </summary>
        public static bool Enable(string username, string password)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, writable: true);
                if (key == null) return false;

                key.SetValue("AutoAdminLogon",    "1",                     RegistryValueKind.String);
                key.SetValue("DefaultUserName",   username,                RegistryValueKind.String);
                key.SetValue("DefaultDomainName", Environment.MachineName, RegistryValueKind.String);

                if (!string.IsNullOrEmpty(password))
                    key.SetValue("DefaultPassword", password, RegistryValueKind.String);
                else
                    try { key.DeleteValue("DefaultPassword", false); } catch { }

                return true;
            }
            catch { return false; }
        }

        /// <summary>Desativa o auto-login e remove a senha do registro.</summary>
        public static bool Disable()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, writable: true);
                if (key == null) return false;
                key.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);
                try { key.DeleteValue("DefaultPassword", false); } catch { }
                return true;
            }
            catch { return false; }
        }
    }
}
