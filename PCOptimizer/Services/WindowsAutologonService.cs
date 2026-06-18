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

        // Trava do Windows 10 2004+/Windows 11: quando = 2 (padrão), força login
        // só por Windows Hello em contas Microsoft e ESCONDE a opção do netplwiz,
        // fazendo o Winlogon ignorar o AutoAdminLogon. Zerar (=0) reabilita o
        // login por senha e é o que faz o auto-login funcionar no Windows 11 25H2.
        private const string PasswordLessPath =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";

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
        /// Reabilita o login por senha desativando a trava "somente Windows Hello"
        /// do Windows 11/10 2004+. Sem isto o AutoAdminLogon é ignorado em contas
        /// Microsoft. Cria a chave se não existir.
        /// </summary>
        private static void EnablePasswordLogin()
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(PasswordLessPath, writable: true);
                key?.SetValue("DevicePasswordLessBuildVersion", 0, RegistryValueKind.DWord);
            }
            catch { /* sem permissão: o Enable ainda tenta, mas pode não bastar no Win11 */ }
        }

        /// <summary>
        /// Ativa o auto-login. Não valida internamente — chame ValidateCredentials antes.
        /// </summary>
        public static bool Enable(string username, string password)
        {
            try
            {
                // PASSO 1 (crítico no Win11 25H2): reabilita login por senha.
                EnablePasswordLogin();

                using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, writable: true);
                if (key == null) return false;

                key.SetValue("AutoAdminLogon",    "1",                     RegistryValueKind.String);
                key.SetValue("DefaultUserName",   username,                RegistryValueKind.String);
                key.SetValue("DefaultDomainName", Environment.MachineName, RegistryValueKind.String);

                if (!string.IsNullOrEmpty(password))
                    key.SetValue("DefaultPassword", password, RegistryValueKind.String);
                else
                    try { key.DeleteValue("DefaultPassword", false); } catch { }

                // Remove travas que fariam o auto-login valer só algumas vezes ou
                // exibir a tela de bloqueio antes da área de trabalho.
                try { key.DeleteValue("AutoLogonCount", false); } catch { }
                try { key.DeleteValue("ForceAutoLogon", false); } catch { }

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
