using System;
using System.Collections.Generic;
using System.Windows.Input;
using WpfApp.Commands;
using WpfApp.Services;
using System.Windows;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Web;

namespace WpfApp.ViewModels
{
    public class FeedbackViewModel : ViewModelBase
    {
        private readonly LogManager _logger = LogManager.Instance;
        private readonly MainViewModel _mainViewModel;
        private string _feedbackContent = string.Empty;
        private int _selectedFeedbackType = 0;
        private bool _isSubmitting = false;

        // GitHub仓库信息
        private const string GITHUB_REPO_OWNER = "Cassianvale"; // GitHub用户名
        private const string GITHUB_REPO_NAME = "LingYaoKeys"; // 仓库名

        // QQ群号
        private const string QQ_GROUP_NUMBER = "707354859"; // QQ群号

        // 反馈类型列表
        public List<string> FeedbackTypes { get; } = new List<string>
        {
            "Bug反馈",
            "功能建议",
            "使用问题",
            "其他"
        };

        // 不同类型的反馈模板
        private static readonly Dictionary<int, string> FEEDBACK_TEMPLATES = new Dictionary<int, string>
        {
            // Bug反馈模板
            [0] = @"### 问题描述
请详细描述您遇到的Bug：


### 复现步骤
1. 
2. 
3. 

### 预期行为
请描述正常情况下应该发生的结果：


### 实际行为
请描述实际发生的情况：


### 错误信息
如果有错误提示，请粘贴在这里：


### 其他信息
- 使用场景：
- 发生频率：
- 是否必现：
- 其他补充：

",
            // 功能建议模板
            [1] = @"### 功能描述
请详细描述您期望的新功能：


### 使用场景
请描述这个功能会在什么场景下使用：


### 预期效果
请描述这个功能应该如何工作：


### 参考示例
如果有类似功能的参考，请在这里说明：


### 其他信息
请补充其他有助于理解该功能的信息：

",
            // 使用问题模板
            [2] = @"### 问题描述
请描述您在使用过程中遇到的问题：


### 使用场景
请描述在什么情况下遇到这个问题：


### 已尝试的解决方法
请描述您已经尝试过的解决方法：


### 期望的帮助
请描述您需要什么样的帮助：


### 其他信息
请提供其他可能有帮助的信息：

",
            // 其他问题模板
            [3] = @"### 问题描述
请详细描述您的问题或建议：


### 补充信息
请提供任何有助于我们理解的补充信息：


### 其他说明
如有其他需要说明的内容，请在这里补充：

"
        };

        // 选中的反馈类型
        public int SelectedFeedbackType
        {
            get => _selectedFeedbackType;
            set
            {
                if (SetProperty(ref _selectedFeedbackType, value))
                {
                    // 当反馈类型改变时，更新模板
                    UpdateFeedbackTemplate();
                }
            }
        }

        private void UpdateFeedbackTemplate()
        {
            // 如果当前内容为空或者是模板内容，则更新为新的模板
            if (string.IsNullOrWhiteSpace(FeedbackContent) || 
                FEEDBACK_TEMPLATES.Values.Any(template => FeedbackContent.Trim() == template.Trim()))
            {
                FeedbackContent = FEEDBACK_TEMPLATES[SelectedFeedbackType];
            }
        }

        // 反馈内容
        public string FeedbackContent
        {
            get => _feedbackContent;
            set => SetProperty(ref _feedbackContent, value);
        }

        // 是否正在提交
        public bool IsSubmitting
        {
            get => _isSubmitting;
            private set
            {
                if (SetProperty(ref _isSubmitting, value))
                {
                    OnPropertyChanged(nameof(CanSubmit));
                }
            }
        }

        // 是否可以提交
        public bool CanSubmit => !IsSubmitting && !string.IsNullOrWhiteSpace(FeedbackContent);

        // 提交命令
        public ICommand SubmitCommand { get; }

        // 清空命令
        public ICommand ClearCommand { get; }

        // 添加GitHub提交命令
        public ICommand SubmitToGitHubCommand { get; }

        // 添加QQ群命令
        public ICommand JoinQQGroupCommand { get; }

        public FeedbackViewModel(MainViewModel mainViewModel)
        {
            _mainViewModel = mainViewModel;
            SubmitCommand = new RelayCommand(SubmitFeedbackAsync, () => CanSubmit);
            ClearCommand = new RelayCommand(ClearFeedback);
            SubmitToGitHubCommand = new RelayCommand(OpenGitHubIssue, () => !string.IsNullOrWhiteSpace(FeedbackContent));
            JoinQQGroupCommand = new RelayCommand(OpenQQGroup);
            
            // 初始化为默认模板（Bug反馈）
            FeedbackContent = FEEDBACK_TEMPLATES[0];
        }

        private void OpenGitHubIssue()
        {
            try
            {
                string issueTitle = $"[{FeedbackTypes[SelectedFeedbackType]}] {GetFirstLine(FeedbackContent)}";
                string issueBody = GenerateIssueBody();
                
                string githubUrl = $"https://github.com/{GITHUB_REPO_OWNER}/{GITHUB_REPO_NAME}/issues/new?";
                githubUrl += $"title={HttpUtility.UrlEncode(issueTitle)}";
                githubUrl += $"&body={HttpUtility.UrlEncode(issueBody)}";

                Process.Start(new ProcessStartInfo
                {
                    FileName = githubUrl,
                    UseShellExecute = true
                });

                _mainViewModel.UpdateStatusMessage("已打开GitHub Issues页面");
            }
            catch (Exception ex)
            {
                _logger.LogError("GitHub", "打开GitHub Issues失败", ex);
                _mainViewModel.UpdateStatusMessage("打开GitHub Issues失败", true);
            }
        }

        private void OpenQQGroup()
        {
            try
            {
                // 使用QQ的群链接格式
                string qqUrl = $"https://qm.qq.com/cgi-bin/qm/qr?k=xxxxx"; // 替换为实际的QQ群链接
                Process.Start(new ProcessStartInfo
                {
                    FileName = qqUrl,
                    UseShellExecute = true
                });

                _mainViewModel.UpdateStatusMessage("已打开QQ群链接");
            }
            catch (Exception ex)
            {
                _logger.LogError("QQ", "打开QQ群链接失败", ex);
                _mainViewModel.UpdateStatusMessage("打开QQ群链接失败，请手动加群：" + QQ_GROUP_NUMBER, true);
            }
        }

        private string GetFirstLine(string text)
        {
            // 获取第一个非空的描述行作为标题
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith("###")) continue; // 跳过标题行
                var content = line.Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return content.Length > 50 ? content.Substring(0, 47) + "..." : content;
                }
            }
            return "新建反馈";
        }

        private string GenerateIssueBody()
        {
            return $@"### 反馈类型
{FeedbackTypes[SelectedFeedbackType]}

{FeedbackContent.Trim()}

---
### 环境信息
- 操作系统：Windows {Environment.OSVersion.Version}
- 应用版本：{GetApplicationVersion()}
- 提交时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}
";
        }

        private string GetApplicationVersion()
        {
            try
            {
                return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "未知";
            }
            catch
            {
                return "未知";
            }
        }

        // 提交反馈
        private async void SubmitFeedbackAsync()
        {
            if (string.IsNullOrWhiteSpace(FeedbackContent))
            {
                _mainViewModel.UpdateStatusMessage("请输入反馈内容", true);
                return;
            }

            try
            {
                IsSubmitting = true;
                _mainViewModel.UpdateStatusMessage("正在提交反馈...");

                // 记录反馈信息
                _logger.LogDebug("Feedback", $"提交反馈 - 类型: {FeedbackTypes[SelectedFeedbackType]}, " +
                    $"内容长度: {FeedbackContent.Length}");

                // TODO: 实现实际的反馈提交逻辑
                await Task.Delay(1000); // 模拟网络请求

                // 清空表单
                ClearFeedback();
                
                _mainViewModel.UpdateStatusMessage("反馈提交成功，感谢您的反馈！");
            }
            catch (Exception ex)
            {
                _logger.LogError("Feedback", "提交反馈失败", ex);
                _mainViewModel.UpdateStatusMessage("提交反馈失败，请稍后重试", true);
            }
            finally
            {
                IsSubmitting = false;
            }
        }

        // 清空反馈表单
        private void ClearFeedback()
        {
            FeedbackContent = FEEDBACK_TEMPLATES[SelectedFeedbackType];
        }
    }
} 