using System;
using System.Threading;
using System.Windows;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Linq;

/// <summary>
/// DD虚拟键盘驱动服务类
/// 核心功能：
/// 1. 驱动初始化与状态管理
///    - 加载DD驱动动态链接库
///    - 初始化驱动环境
///    - 管理驱动启用/禁用状态
///    - 提供驱动状态变化事件通知
///
/// 2. 按键操作控制
///    - 模拟按键按下/释放
///    - 执行按键组合操作
///    - 支持按键序列自动执行
///    - 控制按键触发间隔时间
///
/// 3. 按键模式管理
///    - 顺序模式：按设定顺序循环执行按键序列
///    - 按压模式：按住触发键时持续执行指定按键
///    - 模式切换与状态维护
/// </summary>
namespace WpfApp.Services
{
    public class DDDriverService
    {
        private CDD _dd;
        private bool _isInitialized;
        private bool _isEnabled;
        private bool _isSequenceMode;
        private List<DDKeyCode> _keyList = new List<DDKeyCode>();
        private bool _isHoldMode;
        private int _keyInterval = 50;
        private int _currentKeyIndex = 0;
        private readonly object _lockObject = new object();
        private Task? _sequenceTask;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly Stopwatch _sequenceStopwatch = new Stopwatch();
        private readonly Stopwatch _operationTimer = new Stopwatch();
        private readonly Queue<TimeSpan> _keyPressDurations = new Queue<TimeSpan>();
        private readonly Queue<TimeSpan> _keyIntervals = new Queue<TimeSpan>();
        private DateTime _cycleStartTime;
        private int _totalKeyPresses;
        private const int MAX_TIMING_SAMPLES = 100;

        public event EventHandler<bool>? InitializationStatusChanged;
        public event EventHandler<bool>? EnableStatusChanged;
        public event EventHandler<StatusMessageEventArgs>? StatusMessageChanged;
        public event EventHandler<int>? KeyIntervalChanged;
        private readonly LogManager _logger = LogManager.Instance;

        // 性能统计事件
        public event EventHandler<PerformanceMetrics>? PerformanceMetricsUpdated;

        // DD驱动服务构造函数
        public DDDriverService()
        {
            _dd = new CDD();
            _isInitialized = false;
            _isEnabled = false;
        }

        // 添加公共属性用于检查初始化状态
        public bool IsInitialized => _isInitialized;

        public bool LoadDllFile(string dllPath)
        {
            try
            {
                _logger.LogInitialization("开始", $"加载驱动文件: {dllPath}");
                
                // 1. 清理现有实例
                if(_isInitialized)
                {
                    _dd = new CDD();
                    _isInitialized = false;
                }

                // 2. 加载驱动 - 只检查Load返回值
                int ret = _dd.Load(dllPath);
                if (ret != 1)
                {
                    _logger.LogError("LoadDllFile", $"驱动加载失败，返回值: {ret}");
                    return false;
                }

                // 3. 初始化驱动
                ret = _dd.btn(0);
                if (ret != 1)
                {
                    _logger.LogError("LoadDllFile", "驱动初始化失败");
                    MessageBox.Show("驱动初始化失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    SendStatusMessage("驱动初始化失败", true);
                    return false;
                }

                // 5. 设置初始化标志
                _isInitialized = true;
                InitializationStatusChanged?.Invoke(this, true);
                SendStatusMessage("驱动加载成功");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("LoadDllFile", "驱动加载异常", ex);
                return false;
            }
        }

        // 是否启用
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    EnableStatusChanged?.Invoke(this, value);
                    
                    if (_isEnabled)
                    {
                        // 使用 Fire-and-forget 模式启动序列
                        _ = StartKeySequenceAsync().ContinueWith(t =>
                        {
                            if (t.Exception != null)
                            {
                                _logger.LogError("StartKeySequence", "按键序列启动异常", t.Exception);
                                _isEnabled = false;
                            }
                        });
                    }
                    else
                    {
                        // 使用 Fire-and-forget 模式停止序列
                        _ = StopKeySequenceAsync().ContinueWith(t =>
                        {
                            if (t.Exception != null)
                            {
                                _logger.LogError("StartKeySequence", "按键序列停止异常", t.Exception);
                            }
                        });
                    }
                }
            }
        }

        // 异步启动序列
        private async Task StartKeySequenceAsync()
        {
            try
            {
                await StopKeySequenceAsync();
                InitializeSequence();
                LogSequenceStart();

                _cancellationTokenSource = new CancellationTokenSource();
                var token = _cancellationTokenSource.Token;

                if (_isSequenceMode)
                {
                    _sequenceTask = Task.Run(async () =>
                    {
                        while (!token.IsCancellationRequested && _keyList.Count > 0)
                        {
                            try
                            {
                                await ExecuteSequenceCycle(token);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError("StartKeySequence", "按键序列执行异常", ex);
                                break;
                            }
                        }
                    }, token);

                    await Task.Delay(1);
                }
                else if (_isHoldMode)
                {
                    await ExecuteHoldMode(token);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("StartKeySequence", "启动按键序列异常", ex);
                _isEnabled = false;
            }
        }

        // 初始化序列
        private void InitializeSequence()
        {
            _sequenceStopwatch.Restart();
            _operationTimer.Restart();
            _keyPressDurations.Clear();
            _keyIntervals.Clear();
            _totalKeyPresses = 0;
            _cycleStartTime = DateTime.Now;
        }

        // 序列周期执行
        private async Task ExecuteSequenceCycle(CancellationToken token)
        {
            var cycleStart = DateTime.Now;
            DDKeyCode keyCode;
            
            lock (_lockObject)
            {
                keyCode = _keyList[_currentKeyIndex];
                _currentKeyIndex = (_currentKeyIndex + 1) % _keyList.Count;
            }

            await Task.Run(() => ExecuteKeyCycle(keyCode));
            
            var elapsed = DateTime.Now - cycleStart;
            var remainingDelay = _keyInterval - (int)elapsed.TotalMilliseconds;
            
            if (remainingDelay > 0)
            {
                await Task.Delay(remainingDelay, token);
            }
            
            var actualInterval = DateTime.Now - _cycleStartTime;
            RecordKeyInterval(actualInterval);
            
            _cycleStartTime = DateTime.Now;
        }

        // 按压模式执行
        private async Task ExecuteHoldMode(CancellationToken token)
        {
            _sequenceTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var keyCode in _keyList)
                        {
                            if (token.IsCancellationRequested) break;
                            await Task.Run(() => SendKey(keyCode, true));
                        }
                        await Task.Delay(10, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("StartKeySequence", "按压模式执行异常", ex);
                        break;
                    }
                }
            }, token);

            await Task.Delay(1);
        }

        // 异步停止
        private async Task StopKeySequenceAsync()
        {
            try
            {
                _sequenceStopwatch.Stop();
                LogSequenceEnd();

                if (_cancellationTokenSource != null)
                {
                    await Task.Run(() => _cancellationTokenSource.Cancel());
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }

                if (_sequenceTask != null)
                {
                    try
                    {
                        await Task.WhenAny(_sequenceTask, Task.Delay(1000)); // 设置超时时间
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("StopKeySequence", "等待序列任务完成异常", ex);
                    }
                    _sequenceTask = null;
                }

                _currentKeyIndex = 0;

                // 创建一个按键列表的副本来进行遍历
                // 确保即使某个按键释放失败，其他按键的释放操作仍会继续
                if (_keyList != null && _keyList.Any())
                {
                    // 创建一个副本来避免集合修改冲突
                    var keyListCopy = _keyList.ToList();
                    foreach (var keyCode in keyListCopy)
                    {
                        try 
                        {
                            await Task.Run(() => SendKey(keyCode, false));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError("StopKeySequence", $"释放按键 {keyCode} 失败", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("StopKeySequence", "停止按键序列异常", ex);
            }
        }

        // 添加一个新的方法用于执行单个按键的完整周期
        private bool ExecuteKeyCycle(DDKeyCode keyCode)
        {
            try
            {
                _cycleStartTime = DateTime.Now;
                var keyDownTime = _cycleStartTime;
                
                // 按下按键
                if (!SendKey(keyCode, true))
                {
                    _logger.LogWarning("ExecuteKeyCycle", $"按键按下失败: {keyCode}");
                    return false;
                }
                
                // 释放按键
                var keyUpTime = DateTime.Now;
                if (!SendKey(keyCode, false))
                {
                    _logger.LogWarning("ExecuteKeyCycle", $"按键释放失败: {keyCode}");
                    return false;
                }

                // 记录按压时长
                var pressDuration = keyUpTime - keyDownTime;
                RecordKeyPressDuration(pressDuration);

                _totalKeyPresses++;
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("ExecuteKeyCycle", "执行按键周期异常", ex);
                return false;
            }
        }

        // 记录按键按压时长
        private void RecordKeyPressDuration(TimeSpan duration)
        {
            _keyPressDurations.Enqueue(duration);
            if (_keyPressDurations.Count > MAX_TIMING_SAMPLES)
            {
                _keyPressDurations.Dequeue();
            }
        }

        // 记录按键间隔
        private void RecordKeyInterval(TimeSpan interval)
        {
            if (interval.TotalMilliseconds > 0)
            {
                _keyIntervals.Enqueue(interval);
                if (_keyIntervals.Count > MAX_TIMING_SAMPLES)
                {
                    _keyIntervals.Dequeue();
                }
            }
        }

        // 模拟按键组合（如Ctrl+Alt+Del）
        public async Task SimulateKeyComboAsync(params DDKeyCode[] keyCodes)
        {
            if (!_isInitialized || _dd.key == null) return;

            try
            {
                // 按下所有键
                foreach (var key in keyCodes)
                {
                    _dd.key((int)key, 1);
                    await Task.Delay(5);
                }

                await Task.Delay(50);

                // 释放所有键（反序）
                for (int i = keyCodes.Length - 1; i >= 0; i--)
                {
                    _dd.key((int)keyCodes[i], 2);
                    await Task.Delay(5);
                }
            }
            catch (Exception ex)
            {
                SendStatusMessage($"模拟按键异常：{ex.Message}", true);
            }
        }

        // 输入文本
        public bool SimulateText(string text)
        {
            if (!_isInitialized || _dd.str == null)
            {
                _logger.LogError("SimulateText", "驱动未初始化或str函数指针为空");
                return false;
            }
            try
            {
                return _dd.str(text) == 1;
            }
            catch (Exception ex)
            {
                _logger.LogError("SimulateText", "模拟文本输入异常", ex);
                return false;
            }
        }

        // 鼠标相关方法
        public bool MouseClick(DDMouseButton button, int delayMs = 50)
        {
            if (!_isInitialized || _dd.btn == null)
            {
                _logger.LogError("MouseOperation", "驱动未初始化或btn函数指针为空");
                return false;
            }

            // 根据文档说明设置按键代码
            (int down, int up) = button switch
            {
                DDMouseButton.Left => (1, 2),      // 左键：按下=1，放开=2
                DDMouseButton.Right => (4, 8),      // 右键：按下=4，放开=8
                DDMouseButton.Middle => (16, 32),   // 中键：按下=16，放开=32
                DDMouseButton.XButton1 => (64, 128), // 4键：按下=64，放开=128
                DDMouseButton.XButton2 => (256, 512), // 5键：按下=256，放开=512
                _ => (0, 0)
            };

            if (down == 0) return false;

            try
            {
                // 按下按键
                int ret = _dd.btn(down);
                if (ret != 1)
                {
                    _logger.LogWarning("MouseOperation", $"鼠标按下失败，返回值：{ret}");
                    return false;
                }

                // 延迟
                Thread.Sleep(delayMs);

                // 释放按键
                ret = _dd.btn(up);
                if (ret != 1)
                {
                    _logger.LogWarning("MouseOperation", $"鼠标释放失败，返回值：{ret}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("MouseOperation", "鼠标点击异常", ex);
                return false;
            }
        }

        // 鼠标移动(绝对坐标)以屏幕左上角为原点
        public bool MoveMouse(int x, int y)
        {
            if (!_isInitialized || _dd.mov == null)
            {
                _logger.LogError("MoveMouse", "驱动未初始化或mov函数指针为空");
                return false;
            }

            try
            {
                int ret = _dd.mov(x, y);
                if (ret != 1)
                {
                    _logger.LogWarning("MoveMouse", $"鼠标移动失败，返回值：{ret}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("MoveMouse", "鼠标移动异常", ex);
                return false;
            }
        }

        // 滚轮操作：1=前滚，2=后滚
        public bool MouseWheel(bool isForward)
        {
            if (!_isInitialized || _dd.whl == null)
            {
                _logger.LogError("MouseWheel", "驱动未初始化或whl函数指针为空");
                return false;
            }

            try
            {
                int ret = _dd.whl(isForward ? 1 : 2);
                if (ret != 1)
                {
                    _logger.LogWarning("MouseWheel", $"鼠标滚轮操作失败，返回值：{ret}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("MouseWheel", "鼠标滚轮操作异常", ex);
                return false;
            }
        }

        // 键盘按键
        public bool SendKey(DDKeyCode keyCode, bool isKeyDown)
        {
            if (!_isInitialized || _dd.key == null)
            {
                _logger.LogError("SendKey", "驱动未初始化或key函数指针为空");
                return false;
            }

            try
            {
                if (!KeyCodeMapping.IsValidDDKeyCode(keyCode))
                {
                    _logger.LogError("SendKey", $"无效的DD键码: {keyCode} ({(int)keyCode})");
                    return false;
                }
                
                int ddCode = (int)keyCode;
                int ret = _dd.key(ddCode, isKeyDown ? 1 : 2);
                
                _logger.LogKeyOperation(keyCode, isKeyDown, ret);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("SendKey", "按键操作异常", ex);
                return false;
            }
        }

        // 从虚拟键码发送按键的方法
        public bool SendVirtualKey(int virtualKeyCode, bool isKeyDown)
        {
            if (!_isInitialized || _dd.key == null) return false;

            try
            {
                // 用优化后的映射获取DD键码
                DDKeyCode ddKeyCode = KeyCodeMapping.GetDDKeyCode(virtualKeyCode, this);
                if (ddKeyCode == DDKeyCode.None) return false;

                // 显式转换为int
                int ret = _dd.key((int)ddKeyCode, isKeyDown ? 1 : 2);
                return ret == 1;
            }
            catch (Exception ex)
            {
                _logger.LogError("SendVirtualKey", "虚拟按键操作异常", ex);
                return false;
            }
        }

        // 模拟按键按下和释放的完整过程
        public bool SimulateKeyPress(DDKeyCode keyCode, int? customDelay = null)
        {
            if (!_isInitialized) return false;
            
            try
            {
                // 使用自定义延迟或配置的间隔
                int delayMs = customDelay ?? _keyInterval;
                
                // 按下按键
                if (!SendKey(keyCode, true))
                {
                    return false;
                }

                // 延迟
                Thread.Sleep(delayMs);

                // 释放按键
                return SendKey(keyCode, false);
            }
            catch (Exception ex)
            {
                _logger.LogError("SimulateKeyPress", "模拟按键异常", ex);
                return false;
            }
        }

        // 释放资源
        public void Dispose()
        {
            try
            {
                _logger.LogDebug("Dispose", "开始释放驱动资源");
                
                // 先停止所有正在进行的操作
                IsEnabled = false;
                
                // 等待任务完成
                if (_sequenceTask != null)
                {
                    _cancellationTokenSource?.Cancel();
                    try
                    {
                        _sequenceTask.Wait(1000); // 等待最多1秒
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Dispose", $"等待序列任务完成超时: {ex.Message}");
                    }
                }

                // 确保所有按键都被释放
                if (_isInitialized && _dd.key != null)
                {
                    try
                    {
                        // 创建一个按键列表的副本来进行遍历
                        if (_keyList != null && _keyList.Any())
                        {
                            var keyListCopy = new List<DDKeyCode>(_keyList);
                            foreach (var keyCode in keyListCopy)
                            {
                                _dd.key((int)keyCode, 2); // 释放所有按键
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Dispose", $"释放按键状态异常: {ex.Message}");
                    }
                }

                // 清理资源
                _cancellationTokenSource?.Dispose();
                _sequenceTask = null;
                
                // 清理所有状态
                _isEnabled = false;
                _isSequenceMode = false;
                _isHoldMode = false;
                _keyList.Clear();
                _currentKeyIndex = 0;

                // 最后再清理初始化状态和驱动实例
                _isInitialized = false;
                _dd = new CDD();

                _logger.LogDebug("Dispose", "驱动资源释放完成");
            }
            catch (Exception ex)
            {
                _logger.LogError("Dispose", "释放资源时发生异常", ex);
            }
        }

        // 顺序模式&按压模式逻辑
        // 添加属性用于设置按键模式和按键列表
        public bool IsSequenceMode
        {
            get => _isSequenceMode;
            set => _isSequenceMode = value;
        }

        // 设置按键列表
        public void SetKeyList(List<DDKeyCode> keyList)
        {
            _keyList = keyList ?? new List<DDKeyCode>();
            _currentKeyIndex = 0;
        }

        // 设置按键间隔
        public void SetKeyInterval(int interval)
        {
            KeyInterval = Math.Max(1, interval);
        }

        // 添加方法用于控制按压模式
        public void SetHoldMode(bool isHold)
        {
            _isHoldMode = isHold;
            if (!_isSequenceMode)
            {
                _isEnabled = isHold;
                if (isHold)
                {
                    StartKeySequenceAsync();
                }
                else
                {
                    StopKeySequenceAsync();
                }
            }
        }

        // 模拟带修饰键的按键
        public async Task SimulateKeyWithModifiersAsync(DDKeyCode keyCode, KeyModifiers modifiers)
        {
            if (!_isInitialized) return;

            var modifierKeys = new List<DDKeyCode>();
            
            // 添加修饰键
            if (modifiers.HasFlag(KeyModifiers.Control))
                modifierKeys.Add(DDKeyCode.LEFT_CTRL);
            if (modifiers.HasFlag(KeyModifiers.Alt))
                modifierKeys.Add(DDKeyCode.LEFT_ALT);
            if (modifiers.HasFlag(KeyModifiers.Shift))
                modifierKeys.Add(DDKeyCode.LEFT_SHIFT);
            if (modifiers.HasFlag(KeyModifiers.Windows))
                modifierKeys.Add(DDKeyCode.LEFT_WIN);

            try
            {
                // 按下所有修饰键
                foreach (var modifier in modifierKeys)
                {
                    SendKey(modifier, true);
                    await Task.Delay(5);
                }

                // 按下主键
                SendKey(keyCode, true);
                await Task.Delay(50);
                SendKey(keyCode, false);

                // 释放所有修饰键(反序)
                for (int i = modifierKeys.Count - 1; i >= 0; i--)
                {
                    SendKey(modifierKeys[i], false);
                    await Task.Delay(5);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("SimulateKeyWithModifiers", "模拟组合键异常", ex);
                // 确保释放所有按键
                foreach (var modifier in modifierKeys)
                {
                    SendKey(modifier, false);
                }
            }
        }

        // 检查修饰键状态
        public bool IsModifierKeyPressed(KeyModifiers modifier)
        {
            if (!_isInitialized) return false;

            try
            {
                switch (modifier)
                {
                    case KeyModifiers.Control:
                        return IsKeyPressed(DDKeyCode.LEFT_CTRL) || IsKeyPressed(DDKeyCode.RIGHT_CTRL);
                    case KeyModifiers.Alt:
                        return IsKeyPressed(DDKeyCode.LEFT_ALT) || IsKeyPressed(DDKeyCode.RIGHT_ALT);
                    case KeyModifiers.Shift:
                        return IsKeyPressed(DDKeyCode.LEFT_SHIFT) || IsKeyPressed(DDKeyCode.RIGHT_SHIFT);
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("IsModifierKeyPressed", "检查修饰键状态异常", ex);
                return false;
            }
        }

        // 检查按键是否按下
        private bool IsKeyPressed(DDKeyCode keyCode)
        {
            if (_dd.key == null) return false;
            return _dd.key((int)keyCode, 3) == 1; // 3表示检查按键状态
        }

        // 执行按键序列
        public async Task ExecuteKeySequenceAsync(IEnumerable<DDKeyCode> keySequence, int? customInterval = null)
        {
            if (!_isInitialized) return;

            foreach (var keyCode in keySequence)
            {
                if (!_isEnabled) break;

                try
                {
                    await Task.Run(() =>
                    {
                        SendKey(keyCode, true);
                        Thread.Sleep(customInterval ?? _keyInterval);
                        SendKey(keyCode, false);
                        Thread.Sleep(customInterval ?? _keyInterval);
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError("ExecuteKeySequence", "执行按键序列异常", ex);
                    break;
                }
            }
        }

        // 检查驱动是否就绪
        public bool IsReady => _isInitialized && _dd.key != null;

        // 将Windows虚拟键码转换为DD键码
        public int? ConvertVirtualKeyToDDCode(int vkCode)
        {
            if (!_isInitialized || _dd.todc == null)
            {
                _logger.LogError("ConvertVirtualKeyToDDCode", "驱动未初始化或todc函数指针为空");
                return null;
            }

            try
            {
                int ddCode = _dd.todc(vkCode);
                if (ddCode <= 0)
                {
                    _logger.LogWarning("ConvertVirtualKeyToDDCode", $"虚拟键码转换失败: {vkCode}");
                    return null;
                }
                return ddCode;
            }
            catch (Exception ex)
            {
                _logger.LogError("ConvertVirtualKeyToDDCode", "虚拟键码转换异常", ex);
                return null;
            }
        }

        // 发送状态消息的方法
        private void SendStatusMessage(string message, bool isError = false)
        {
            if (isError)
                _logger.LogError("DDDriver", message);
            else
                _logger.LogDriverEvent("Status", message);
        }

        // 修改现有的按键间隔字段和属性
        public int KeyInterval
        {
            get => _keyInterval;
            set
            {
                if (value != _keyInterval && value >= 1)
                {
                    _keyInterval = value;
                    KeyIntervalChanged?.Invoke(this, value);
                    _logger.LogSequenceEvent("KeyInterval", $"按键间隔已更新为: {value}ms");
                }
            }
        }

        // 添加性能统计相关方法
        private void UpdatePerformanceMetrics()
        {
            var metrics = new PerformanceMetrics
            {
                AverageKeyPressTime = CalculateAverage(_keyPressDurations),
                AverageKeyInterval = CalculateAverage(_keyIntervals),
                TotalExecutionTime = _sequenceStopwatch.Elapsed,
                TotalKeyPresses = _totalKeyPresses,
                CurrentSequence = string.Join(", ", _keyList)
            };

            PerformanceMetricsUpdated?.Invoke(this, metrics);
        }

        // 添加辅助计算方法
        private double CalculateAverage(Queue<TimeSpan> timings)
        {
            return timings.Count > 0 ? timings.Average(t => t.TotalMilliseconds) : 0;
        }

        private void LogSequenceStart()
        {
            var details = $"模式: {(_isSequenceMode ? "顺序模式" : "按压模式")} | " +
                         $"按键列表: {string.Join(", ", _keyList)} | " +
                         $"间隔: {_keyInterval}ms";
            _logger.LogSequenceEvent("开始", details);
        }

        private void LogSequenceEnd()
        {
            var avgPressDuration = _keyPressDurations.Count > 0 
                ? _keyPressDurations.Average(t => t.TotalMilliseconds) 
                : 0;
            
            var avgInterval = _keyIntervals.Count > 0 
                ? _keyIntervals.Average(t => t.TotalMilliseconds) 
                : 0;

            var details = $"\n├─ 执行时间: {_sequenceStopwatch.Elapsed.TotalSeconds:F2}s\n" +
                         $"├─ 总按键次数: {_totalKeyPresses}\n" +
                         $"├─ 平均按压时长: {avgPressDuration:F2}ms\n" +
                         $"├─ 平均实际间隔: {avgInterval:F2}ms\n" +
                         $"└─ 设定间隔: {_keyInterval}ms";
            
            _logger.LogSequenceEvent("结束", details);
        }
    }

    // 重命名鼠标按键枚举
    public enum DDMouseButton
    {
        Left,   // 左键
        Right,  // 右键
        Middle, // 中键
        XButton1, // 侧键1
        XButton2 // 侧键2
    }

    // 添加事件定义，向UI传递消息
    public class StatusMessageEventArgs : EventArgs
    {
        public string Message { get; }
        public bool IsError { get; }
        public StatusMessageEventArgs(string message, bool isError = false)
        {
            Message = message;
            IsError = isError;
        }
    }
} 