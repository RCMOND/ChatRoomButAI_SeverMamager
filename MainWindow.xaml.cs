using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.AspNetCore.SignalR.Client;
using ServerManager.Models;

namespace ServerManager;

public partial class MainWindow : Window
{
    private HubConnection? _adminHubConnection;
    private readonly HttpClient _httpClient = new HttpClient();
    private string? _jwtToken = null;
    private System.Windows.Threading.DispatcherTimer? _refreshTimer;

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public MainWindow()
    {
        InitializeComponent();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    // ==================== 登录/注册 ====================
    private async void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        string username = txtAdminUser.Text.Trim();
        string password = txtAdminPassword.Password;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            MessageBox.Show("请输入用户名和密码");
            return;
        }

        string hashedPassword = ComputeSha256Hash(password);
        string serverUrl = txtServerUrl.Text.TrimEnd('/');

        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{serverUrl}/api/auth/login", new
            {
                Username = username,
                Password = hashedPassword
            });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
                _jwtToken = result?.Token;
                if (!string.IsNullOrEmpty(_jwtToken))
                {
                    _httpClient.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", _jwtToken);
                }
                txtLoginStatus.Text = "登录成功";
                btnLogin.IsEnabled = false;
                btnRegister.IsEnabled = false;
                txtAdminUser.IsEnabled = false;
                txtAdminPassword.IsEnabled = false;
            }
            else
            {
                var errorMsg = await response.Content.ReadAsStringAsync();
                txtLoginStatus.Text = $"登录失败: {errorMsg}";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"请求失败: {ex.Message}");
        }
    }

    private async void BtnRegister_Click(object sender, RoutedEventArgs e)
    {
        string username = txtAdminUser.Text.Trim();
        string password = txtAdminPassword.Password;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            MessageBox.Show("请输入用户名和密码");
            return;
        }

        string hashedPassword = ComputeSha256Hash(password);
        string serverUrl = txtServerUrl.Text.TrimEnd('/');

        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{serverUrl}/api/auth/register", new
            {
                Username = username,
                Password = hashedPassword
            });

            if (response.IsSuccessStatusCode)
            {
                txtLoginStatus.Text = "注册成功，正在自动登录...";
                await LoginAfterRegister(username, hashedPassword);
            }
            else
            {
                var errorMsg = await response.Content.ReadAsStringAsync();
                txtLoginStatus.Text = $"注册失败: {errorMsg}";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"注册请求失败：{ex.Message}");
        }
    }

    private async Task LoginAfterRegister(string username, string hashedPassword)
    {
        string serverUrl = txtServerUrl.Text.TrimEnd('/');
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{serverUrl}/api/auth/login", new
            {
                Username = username,
                Password = hashedPassword
            });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
                _jwtToken = result?.Token;
                if (!string.IsNullOrEmpty(_jwtToken))
                {
                    _httpClient.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", _jwtToken);
                }
                txtLoginStatus.Text = "登录成功";
                btnLogin.IsEnabled = false;
                btnRegister.IsEnabled = false;
                txtAdminUser.IsEnabled = false;
                txtAdminPassword.IsEnabled = false;
            }
            else
            {
                txtLoginStatus.Text = "自动登录失败，请手动登录";
            }
        }
        catch
        {
            txtLoginStatus.Text = "自动登录失败，请手动登录";
        }
    }

    // ==================== 连接/断开 ====================
    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_jwtToken))
        {
            MessageBox.Show("请先登录");
            return;
        }

        string serverUrl = txtServerUrl.Text.TrimEnd('/');
        btnConnect.IsEnabled = false;
        txtConnectionStatus.Text = "连接中...";

        try
        {
            _adminHubConnection = new HubConnectionBuilder()
                .WithUrl($"{serverUrl}/adminHub", options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult(_jwtToken);
                })
                .Build();

            _adminHubConnection.On<string>("Kickout", (message) =>
            {
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(message, "被踢下线", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _adminHubConnection?.StopAsync();
                    ResetConnectionState();
                });
            });

            _adminHubConnection.On<IEnumerable<string>>("OnlineUsersUpdated", (users) =>
            {
                Dispatcher.Invoke(() => _ = LoadOnlineUsersWithStatus());
            });

            _adminHubConnection.On<IEnumerable<object>>("BannedIpsUpdated", (ips) =>
            {
                Dispatcher.Invoke(() =>
                {
                    lvBannedIps.ItemsSource = ips.Select(i => new BannedIpViewModel
                    {
                        Ip = ((dynamic)i).Ip,
                        Reason = ((dynamic)i).Reason
                    }).ToList();
                });
            });

            _adminHubConnection.Reconnecting += error =>
            {
                Dispatcher.Invoke(() => txtConnectionStatus.Text = "重连中...");
                return Task.CompletedTask;
            };
            _adminHubConnection.Closed += error =>
            {
                Dispatcher.Invoke(() =>
                {
                    txtConnectionStatus.Text = "连接已断开";
                    ResetConnectionState();
                });
                return Task.CompletedTask;
            };

            await _adminHubConnection.StartAsync();
            txtConnectionStatus.Text = "已连接";
            txtStatus.Text = $"已连接到 {serverUrl}";

            // 启用操作按钮
            btnDisconnect.IsEnabled = true;
            btnClearMessages.IsEnabled = true;

            // 启动定时刷新
            _refreshTimer = new System.Windows.Threading.DispatcherTimer();
            _refreshTimer.Interval = TimeSpan.FromSeconds(5);
            _refreshTimer.Tick += async (s, args) =>
            {
                await LoadOnlineUsersWithStatus();
                await LoadBannedIps();
                await LoadBannedUsers();
            };
            _refreshTimer.Start();

            // 首次加载数据
            await LoadOnlineUsersWithStatus();
            await LoadPlaylist();
            await LoadLogs();
            await LoadBannedIps();
            await LoadBannedUsers();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"连接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            ResetConnectionState();
        }
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _adminHubConnection?.StopAsync();
        ResetConnectionState();
    }

    private void ResetConnectionState()
    {
        _jwtToken = null;
        _httpClient.DefaultRequestHeaders.Authorization = null;
        btnLogin.IsEnabled = true;
        btnRegister.IsEnabled = true;
        txtAdminUser.IsEnabled = true;
        txtAdminPassword.IsEnabled = true;
        txtLoginStatus.Text = "";
        btnConnect.IsEnabled = true;
        btnDisconnect.IsEnabled = false;
        btnClearMessages.IsEnabled = false;
        txtConnectionStatus.Text = "未连接";
        txtStatus.Text = "未连接";
        lvOnlineUsers.ItemsSource = null;
        lstLogs.ItemsSource = null;
        lvPlaylist.ItemsSource = null;
        lvBannedIps.ItemsSource = null;
        lvBannedUsers.ItemsSource = null;

        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    // ==================== 在线用户管理 ====================
    private async Task LoadOnlineUsersWithStatus()
    {
        if (_adminHubConnection?.State != HubConnectionState.Connected) return;
        try
        {
            var users = await _adminHubConnection.InvokeAsync<List<UserStatusDto>>("GetOnlineUsersWithStatus");
            lvOnlineUsers.ItemsSource = users.Select(u => new UserStatusViewModel
            {
                Username = u.Username,
                IsOnline = u.IsOnline,
                IsMuted = u.IsMuted,
                IsBanned = u.IsBanned
            }).ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载在线用户失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void KickUser_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var username = button?.Tag as string;
        if (string.IsNullOrEmpty(username)) return;
        await _adminHubConnection!.InvokeAsync("KickUser", username);
    }

    private async void MuteUser_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var username = button?.Tag as string;
        if (string.IsNullOrEmpty(username)) return;

        int minutes = 5;
        if (cmbMuteMinutes.SelectedItem is ComboBoxItem item)
        {
            string? text = item.Content.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                text = text.Replace("分", "");
                int.TryParse(text, out minutes);
            }
        }

        await _adminHubConnection!.InvokeAsync("MuteUser", username, minutes);
        MessageBox.Show($"已禁言 {username} {minutes} 分钟");
    }

    private async void BanUser_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var username = button?.Tag as string;
        if (string.IsNullOrEmpty(username)) return;

        var viewModel = lvOnlineUsers.ItemsSource?.Cast<UserStatusViewModel>().FirstOrDefault(u => u.Username == username);
        if (viewModel == null) return;

        if (viewModel.IsBanned)
        {
            await _adminHubConnection!.InvokeAsync("UnbanUser", username);
            MessageBox.Show($"已解封用户 {username}");
        }
        else
        {
            await _adminHubConnection!.InvokeAsync("BanUser", username);

            // 自动获取 IP 并封禁
            var ip = await _adminHubConnection.InvokeAsync<string>("GetUserIp", username);
            if (!string.IsNullOrEmpty(ip))
            {
                await _adminHubConnection.InvokeAsync("BanIp", ip, $"封禁用户 {username} 时自动封禁");
                MessageBox.Show($"已封禁用户 {username}，IP {ip} 已被封禁");
            }
            else
            {
                MessageBox.Show($"已封禁用户 {username}，但未能获取到 IP");
            }
        }

        await LoadOnlineUsersWithStatus();
        await LoadBannedIps();
        await LoadBannedUsers();
    }

    // ==================== 封禁用户列表管理 ====================
    private async Task LoadBannedUsers()
    {
        if (_adminHubConnection?.State != HubConnectionState.Connected) return;
        try
        {
            var users = await _adminHubConnection.InvokeAsync<List<BannedUserDto>>("GetBannedUsers");
            lvBannedUsers.ItemsSource = users.Select(u => new BannedUserViewModel
            {
                Username = u.Username,
                IsBanned = u.IsBanned
            }).ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载封禁用户失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnRefreshBannedUsers_Click(object sender, RoutedEventArgs e) => await LoadBannedUsers();

    private async void UnbanUserFromList_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var username = button?.Tag as string;
        if (string.IsNullOrEmpty(username)) return;

        await _adminHubConnection!.InvokeAsync("UnbanUser", username);
        MessageBox.Show($"已解封用户 {username}");
        await LoadBannedUsers();
        await LoadOnlineUsersWithStatus();
    }

    // ==================== IP 管理 ====================
    private async Task LoadBannedIps()
    {
        if (_adminHubConnection?.State != HubConnectionState.Connected) return;
        try
        {
            var ips = await _adminHubConnection.InvokeAsync<IEnumerable<BannedIpViewModel>>("GetBannedIpList");
            lvBannedIps.ItemsSource = ips;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载被封IP失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnAddBanIp_Click(object sender, RoutedEventArgs e)
    {
        string ip = txtBanIp.Text.Trim();
        if (string.IsNullOrEmpty(ip))
        {
            MessageBox.Show("请输入IP地址");
            return;
        }
        string reason = txtBanReason.Text.Trim();
        await _adminHubConnection!.InvokeAsync("BanIp", ip, reason);
        txtBanIp.Clear();
        txtBanReason.Clear();
        await LoadBannedIps();
    }

    private async void UnbanIp_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        string ip = button?.Tag as string;
        if (string.IsNullOrEmpty(ip)) return;
        await _adminHubConnection!.InvokeAsync("UnbanIp", ip);
        await LoadBannedIps();
    }

    // ==================== 播放列表管理 ====================
    private async Task LoadPlaylist()
    {
        string serverUrl = txtServerUrl.Text.TrimEnd('/');
        try
        {
            var playlist = await _httpClient.GetFromJsonAsync<List<PlaylistItemDto>>(
                $"{serverUrl}/api/admin/playlist", _jsonOptions);
            lvPlaylist.ItemsSource = playlist;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载播放列表失败: {ex.Message}");
        }
    }

    private async void BtnRefreshPlaylist_Click(object sender, RoutedEventArgs e) => await LoadPlaylist();

    private async void DeletePlaylistItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        int itemId = (int)button.Tag;
        string serverUrl = txtServerUrl.Text.TrimEnd('/');

        try
        {
            var response = await _httpClient.DeleteAsync($"{serverUrl}/api/admin/playlist/{itemId}");
            if (response.IsSuccessStatusCode)
            {
                await LoadPlaylist();
            }
            else
            {
                MessageBox.Show($"删除失败，状态码：{response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"请求失败：{ex.Message}");
        }
    }

    // ==================== 清空消息 ====================
    private async void BtnClearMessages_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("确定要清空所有聊天消息吗？此操作不可恢复！", "警告",
                                      MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        string serverUrl = txtServerUrl.Text.TrimEnd('/');
        try
        {
            var response = await _httpClient.DeleteAsync($"{serverUrl}/api/admin/messages");
            if (response.IsSuccessStatusCode)
            {
                MessageBox.Show("清空成功", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                MessageBox.Show("只有管理员 (admin) 才能执行此操作", "权限不足");
            }
            else
            {
                MessageBox.Show($"清空失败，状态码：{response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"请求失败：{ex.Message}");
        }
    }

    // ==================== 公告设置 ====================
    private async void BtnSetAnnouncement_Click(object sender, RoutedEventArgs e)
    {
        string content = txtAnnouncement.Text.Trim();
        if (string.IsNullOrEmpty(content))
        {
            MessageBox.Show("请输入公告内容");
            return;
        }

        string serverUrl = txtServerUrl.Text.TrimEnd('/');
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{serverUrl}/api/admin/announcement", new
            {
                Content = content
            });
            if (response.IsSuccessStatusCode)
            {
                MessageBox.Show("公告已发布");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                MessageBox.Show($"发布失败：{error}");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"请求失败：{ex.Message}");
        }
    }

    private async void BtnClearAnnouncement_Click(object sender, RoutedEventArgs e)
    {
        string serverUrl = txtServerUrl.Text.TrimEnd('/');
        try
        {
            // 发送空内容以清除公告（后端需支持空内容或直接删除记录）
            var response = await _httpClient.PostAsJsonAsync($"{serverUrl}/api/admin/announcement", new
            {
                Content = ""  // 后端应能处理空字符串作为“清除”
            });
            if (response.IsSuccessStatusCode)
            {
                MessageBox.Show("公告已清除");
                txtAnnouncement.Clear();
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                MessageBox.Show($"清除失败：{error}");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"请求失败：{ex.Message}");
        }
    }

    // ==================== 日志 ====================
    private async Task LoadLogs()
    {
        string serverUrl = txtServerUrl.Text.TrimEnd('/');
        try
        {
            var logs = await _httpClient.GetFromJsonAsync<IEnumerable<string>>($"{serverUrl}/api/admin/logs");
            lstLogs.ItemsSource = logs;
        }
        catch (Exception ex)
        {
            lstLogs.ItemsSource = new[] { $"日志加载失败: {ex.Message}" };
        }
    }

    // ==================== 工具方法 ====================
    private static string ComputeSha256Hash(string rawData)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
                builder.Append(bytes[i].ToString("x2"));
            return builder.ToString();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _adminHubConnection?.DisposeAsync();
        _httpClient.Dispose();
        base.OnClosed(e);
    }
}

// ==================== 辅助类 ====================
public class LoginResponse { public string Token { get; set; } = ""; }
public class PlaylistItemDto { public int Id { get; set; } public string Url { get; set; } = ""; public string Title { get; set; } = ""; }
public class UserStatusDto { public string Username { get; set; } = ""; public bool IsOnline { get; set; } public bool IsMuted { get; set; } public bool IsBanned { get; set; } }
public class UserStatusViewModel
{
    public string Username { get; set; } = "";
    public bool IsOnline { get; set; }
    public bool IsMuted { get; set; }
    public bool IsBanned { get; set; }
    public string StatusText => IsBanned ? "已封禁" : IsMuted ? "禁言中" : "在线";
    public string BanButtonText => IsBanned ? "解封" : "封禁";
}
public class BannedIpViewModel { public string Ip { get; set; } = ""; public string Reason { get; set; } = ""; }
public class BannedUserDto { public string Username { get; set; } = ""; public bool IsBanned { get; set; } }
public class BannedUserViewModel { public string Username { get; set; } = ""; public bool IsBanned { get; set; } }