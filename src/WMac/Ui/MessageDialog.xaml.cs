using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WMac
{
    internal partial class MessageDialog : Window
    {
        Action<string> _onResult;
        string _result, _timeoutResult, _countdownFormat;
        DispatcherTimer _timer;
        int _remaining;

        MessageDialog()
        {
            InitializeComponent();
            GlassWindow.Apply(this);
            Closed += OnClosed;
        }

        /// <summary>Muestra un diálogo no modal. <paramref name="buttons"/>[0] es la acción principal.</summary>
        public static MessageDialog Open(string title, string body, string[] buttons, Action<string> onResult,
                                         int countdownSeconds = 0, string countdownFormat = null, string timeoutResult = null)
        {
            var d = new MessageDialog { _onResult = onResult, _timeoutResult = timeoutResult, _countdownFormat = countdownFormat };
            d.Title = "WMac — " + title;
            d.TitleText.Text = title;
            d.BodyText.Text = body;

            for (int i = buttons.Length - 1; i >= 1; i--) d.AddButton(buttons[i], false, i == buttons.Length - 1);
            if (buttons.Length > 0) d.AddButton(buttons[0], true, buttons.Length == 1);

            if (countdownSeconds > 0)
            {
                d._remaining = countdownSeconds;
                d.CountdownText.Visibility = Visibility.Visible;
                d.UpdateCountdown();
                d._timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                d._timer.Tick += (s, e) =>
                {
                    d._remaining--;
                    d.UpdateCountdown();
                    if (d._remaining <= 0)
                    {
                        d._timer.Stop();
                        d._result = d._timeoutResult;
                        d.Close();
                    }
                };
                d._timer.Start();
            }
            d.Show();
            d.Activate();
            return d;
        }

        public static void Info(string title, string body) => Open(title, body, new[] { "Aceptar" }, null);

        void AddButton(string label, bool primary, bool isCancel)
        {
            var b = new Button
            {
                Content = label,
                Style = (Style)FindResource(primary ? "PrimaryButton" : "PillButton"),
                Margin = new Thickness(10, 0, 0, 0),
                IsDefault = primary,
                IsCancel = isCancel
            };
            b.Click += (s, e) => { _result = label; Close(); };
            Buttons.Children.Add(b);
        }

        void UpdateCountdown() => CountdownText.Text = string.Format(_countdownFormat ?? "{0}", _remaining);

        void OnClosed(object sender, EventArgs e)
        {
            _timer?.Stop();
            var cb = _onResult;
            _onResult = null;
            cb?.Invoke(_result ?? _timeoutResult);
        }
    }
}
