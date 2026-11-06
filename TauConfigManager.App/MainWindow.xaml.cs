using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Windows;
using Microsoft.Win32;
using TauConfigManager.Core;

namespace TauConfigManager.App;

public partial class MainWindow : Window
{
    private readonly TauDeviceService _service = new();
    private readonly ObservableCollection<ConfigDiffEntry> _diffs = new();
    private string _selectedPackagePath = "";
    private string _lastBackupPath = "";

    public MainWindow()
    {
        InitializeComponent();
        DiffGrid.ItemsSource = _diffs;
        _service.Log += AppendLog;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LocalAddressBox.Items.Clear();
        foreach (var address in NetworkAddressHelper.GetLocalIPv4Addresses())
        {
            LocalAddressBox.Items.Add(address.ToString());
        }

        if (LocalAddressBox.Items.Count > 0)
        {
            LocalAddressBox.SelectedIndex = 0;
        }

        AppendLog("工具已启动。默认安全模式不会导出 MAC、序列号、license 和硬件探测字段。");
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var info = await _service.ReadDeviceInfoAsync(ReadConnection());
            ShowDeviceInfo(info);
            AppendLog("连接测试完成。");
        });
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var save = new SaveFileDialog
        {
            Filter = "TAU 配置包 (*.taucfg)|*.taucfg",
            FileName = $"TAU_{HostBox.Text}_{DateTime.Now:yyyyMMdd_HHmmss}.taucfg",
        };
        if (save.ShowDialog(this) != true)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await _service.ExportAsync(ReadConnection(), save.FileName, ReadExportOptions());
            LoadPackage(save.FileName);
        });
    }

    private void OpenPackageButton_Click(object sender, RoutedEventArgs e)
    {
        var open = new OpenFileDialog
        {
            Filter = "TAU 配置包 (*.taucfg)|*.taucfg|所有文件 (*.*)|*.*",
        };
        if (open.ShowDialog(this) == true)
        {
            LoadPackage(open.FileName);
        }
    }

    private async void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePackageSelected())
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var diffs = await _service.CompareAsync(ReadConnection(), _selectedPackagePath, ReadExportOptions());
            _diffs.Clear();
            foreach (var diff in diffs)
            {
                _diffs.Add(diff);
            }

            AppendLog($"对比完成：{diffs.Count} 个文件。");
        });
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePackageSelected() || !TryReadLocalAddress(out var localAddress))
        {
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "导入会修改目标 TAU 配置。工具会先备份目标设备当前配置，然后覆盖配置包内的白名单文件。是否继续？",
            "确认导入",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var result = await _service.ImportAsync(ReadConnection(), _selectedPackagePath, localAddress, ReadImportOptions());
            _lastBackupPath = result.BackupPath;
            BackupPathBox.Text = result.BackupPath;
            AppendLog(result.Output);
            AppendLog($"导入删除目标多余配置文件：{result.DeletedFiles}");
            if (!result.FileVerificationPassed && result.VerificationFailures.Count > 0)
            {
                AppendLog("导入后文件校验失败：");
                foreach (var failure in result.VerificationFailures)
                {
                    AppendLog("  " + failure);
                }
            }

            AppendLog(result.Success ? "导入命令执行完成。" : "导入未完成。");
        });
    }

    private async void RollbackButton_Click(object sender, RoutedEventArgs e)
    {
        var backup = string.IsNullOrWhiteSpace(BackupPathBox.Text) ? _lastBackupPath : BackupPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(backup))
        {
            MessageBox.Show(this, "没有可用的备份路径。", "回滚", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this, $"确认从以下备份回滚？\n{backup}", "确认回滚", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var result = await _service.RollbackAsync(ReadConnection(), backup);
            AppendLog(result.Output);
            AppendLog(result.Success ? "回滚命令执行完成。建议重启设备或通过维护流程重启相关服务。" : "回滚失败。");
        });
    }

    private TauConnectionOptions ReadConnection()
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port))
        {
            throw new InvalidOperationException("端口必须是数字。");
        }

        return new TauConnectionOptions
        {
            Host = HostBox.Text.Trim(),
            Port = port,
            UserName = UserBox.Text.Trim(),
            Password = PasswordBox.Password,
            Timeout = TimeSpan.FromSeconds(25),
        };
    }

    private ExportOptions ReadExportOptions()
    {
        return new ExportOptions
        {
            IncludeDeviceIdentity = IdentityBox.IsChecked == true,
            IncludeManagementMac = MacBox.IsChecked == true,
            IncludeHardwareDetection = HardwareBox.IsChecked == true,
            IncludeLicenses = LicenseBox.IsChecked == true,
        };
    }

    private ImportOptions ReadImportOptions()
    {
        return new ImportOptions
        {
            Force = ForceImportBox.IsChecked == true,
            DeleteMissingManagedFiles = DeleteMissingBox.IsChecked == true,
            VerifyFileHashes = VerifyImportBox.IsChecked == true,
            RunPostImportHooks = true,
            ScheduleReboot = RebootAfterImportBox.IsChecked == true,
            PreserveManagementAddress = PreserveAddressBox.IsChecked == true,
        };
    }

    private void LoadPackage(string path)
    {
        var manifest = TauDeviceService.ReadManifest(path);
        _selectedPackagePath = path;
        PackagePathBox.Text = path;
        PackageSummaryBlock.Text =
            $"源设备：{manifest.SourceIp}\n软件：{manifest.SoftwareVersion}\n硬件：{manifest.HardwareVersion}\n硬件识别：{manifest.HardwareProfile}\n导出时间：{manifest.ExportedAt:yyyy-MM-dd HH:mm:ss}";
        PackageHashBlock.Text = $"Payload SHA256：{manifest.PayloadSha256}";
        AppendLog($"已加载配置包：{path}");
    }

    private bool EnsurePackageSelected()
    {
        if (!File.Exists(_selectedPackagePath))
        {
            MessageBox.Show(this, "请先选择或导出配置包。", "配置包", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private bool TryReadLocalAddress(out IPAddress address)
    {
        address = IPAddress.None;
        var selectedAddress = LocalAddressBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(selectedAddress) ||
            !IPAddress.TryParse(selectedAddress, out var parsedAddress))
        {
            MessageBox.Show(this, "请选择本机 IP。导入时 TAU 会通过该地址下载配置包。", "本机 IP", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        address = parsedAddress;
        return true;
    }

    private void ShowDeviceInfo(TauDeviceInfo info)
    {
        DeviceSoftwareBox.Text = info.SoftwareVersion;
        DeviceHardwareBox.Text = info.HardwareVersion;
        DeviceEngineeringBox.Text = info.EngineeringVersion;
        DeviceProfileBox.Text = info.HardwareProfile;
        DeviceIpBox.Text = info.ManagementIp;
        DeviceMacBox.Text = info.ManagementMac;
        DeviceFeatureBox.Text = info.FeatureHash;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        SetBusy(true);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppendLog("错误：" + ex.Message);
            MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        TestButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
        OpenPackageButton.IsEnabled = !busy;
        CompareButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy;
        RollbackButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(message));
            return;
        }

        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
