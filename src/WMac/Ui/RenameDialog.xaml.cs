using System;
using System.Windows;

namespace WMac
{
    internal partial class RenameDialog : Window
    {
        RenameDialog()
        {
            InitializeComponent();
            GlassWindow.Apply(this);
        }

        public static void Ask(string current, int selectLength, Action<string> onOk)
        {
            var d = new RenameDialog();
            d.NameBox.Text = current;
            d.OkButton.Click += (s, e) =>
            {
                string value = d.NameBox.Text;
                d.Close();
                onOk(value);
            };
            d.CancelButton.Click += (s, e) => d.Close();
            d.Loaded += (s, e) =>
            {
                d.NameBox.Focus();
                d.NameBox.Select(0, Math.Max(0, Math.Min(selectLength, current.Length)));
            };
            d.Show();
            d.Activate();
        }
    }
}
