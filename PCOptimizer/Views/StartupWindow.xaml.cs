using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Services;

namespace PCOptimizer.Views
{
    public partial class StartupWindow : Window
    {
        private List<StartupEntry> _allEntries;
        private List<StartupEntry> _originalState;

        public int ChangesApplied { get; private set; }

        public StartupWindow()
        {
            InitializeComponent();
            LoadEntries();
        }

        private void LoadEntries()
        {
            _allEntries = StartupManager.GetStartupEntries() ?? new List<StartupEntry>();
            _originalState = _allEntries.Select(e => new StartupEntry
            {
                Name = e.Name,
                Command = e.Command,
                Source = e.Source,
                IsEnabled = e.IsEnabled
            }).ToList();

            LstStartup.ItemsSource = _allEntries;
            UpdateCount();
        }

        private void UpdateCount()
        {
            int enabled = _allEntries.Count(e => e.IsEnabled);
            TxtCount.Text = $"{enabled} de {_allEntries.Count} programas ativos";
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            var filter = TxtSearch.Text.ToLower();
            if (string.IsNullOrWhiteSpace(filter))
            {
                LstStartup.ItemsSource = _allEntries;
            }
            else
            {
                LstStartup.ItemsSource = _allEntries
                    .Where(entry => (entry.Name ?? "").ToLower().Contains(filter)
                                 || (entry.Command ?? "").ToLower().Contains(filter))
                    .ToList();
            }
        }

        private void CheckBox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateCount();
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            int changes = 0;

            for (int i = 0; i < _allEntries.Count; i++)
            {
                if (_allEntries[i].IsEnabled != _originalState[i].IsEnabled)
                {
                    StartupManager.SetEnabled(_allEntries[i], _allEntries[i].IsEnabled);
                    changes++;
                }
            }

            ChangesApplied = changes;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
