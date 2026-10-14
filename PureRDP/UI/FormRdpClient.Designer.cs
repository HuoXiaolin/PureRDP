using System.Drawing;
using System.Windows.Forms;

namespace PureRDP.UI
{
    partial class FormRdpClient
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            panelRdp = new BufferedPanel();
            lbRdp = new Label();
            panelStartup = new BufferedPanel();
            lblStatusMessage = new TextBox();
            btnConnect = new ModernButton { Kind = ModernButtonKind.Primary };
            btnSettings = new ModernButton();
            panelConnect = new BufferedPanel();
            btnCancel = new ModernButton();
            lbConnecting = new Label();
            panelRdp.SuspendLayout();
            panelStartup.SuspendLayout();
            panelConnect.SuspendLayout();
            SuspendLayout();
            // 
            // panelRdp
            // 
            panelRdp.Controls.Add(lbRdp);
            panelRdp.Dock = DockStyle.Fill;
            panelRdp.Location = new Point(0, 0);
            panelRdp.Name = "panelRdp";
            panelRdp.Size = new Size(964, 511);
            panelRdp.TabIndex = 0;
            // 
            // lbRdp
            // 
            lbRdp.Anchor = AnchorStyles.Bottom;
            lbRdp.Font = new Font(UiDesignTokens.FontFamily, 10F);
            lbRdp.Location = new Point(350, 227);
            lbRdp.Name = "lbRdp";
            lbRdp.Size = new Size(264, 80);
            lbRdp.TabIndex = 4;
            lbRdp.Text = "远程桌面客户端";
            lbRdp.TextAlign = ContentAlignment.MiddleCenter;
            lbRdp.Visible = false;
            // 
            // panelStartup
            // 
            panelStartup.Controls.Add(lblStatusMessage);
            panelStartup.Controls.Add(btnConnect);
            panelStartup.Controls.Add(btnSettings);
            panelStartup.Dock = DockStyle.Fill;
            panelStartup.Location = new Point(0, 0);
            panelStartup.Name = "panelStartup";
            panelStartup.BackColor = UiDesignTokens.PageBackground;
            panelStartup.Size = new Size(964, 511);
            panelStartup.TabIndex = 1;
            // 
            // lblStatusMessage
            // 
            lblStatusMessage.Anchor = AnchorStyles.None;
            lblStatusMessage.BackColor = UiDesignTokens.SurfaceSubtle;
            lblStatusMessage.BorderStyle = BorderStyle.None;
            lblStatusMessage.Cursor = Cursors.IBeam;
            lblStatusMessage.Font = new Font(UiDesignTokens.FontFamily, 10F);
            lblStatusMessage.ForeColor = UiDesignTokens.Danger;
            lblStatusMessage.Location = new Point(202, 367);
            lblStatusMessage.Multiline = true;
            lblStatusMessage.Name = "lblStatusMessage";
            lblStatusMessage.ReadOnly = true;
            lblStatusMessage.Size = new Size(560, 106);
            lblStatusMessage.TabIndex = 2;
            lblStatusMessage.Text = "连接状态";
            lblStatusMessage.TextAlign = HorizontalAlignment.Center;
            lblStatusMessage.Visible = false;
            // 
            // btnConnect
            // 
            btnConnect.Anchor = AnchorStyles.None;
            btnConnect.Font = new Font(UiDesignTokens.FontFamily, 14F);
            btnConnect.Location = new Point(382, 194);
            btnConnect.Name = "btnConnect";
            btnConnect.Size = new Size(200, 50);
            btnConnect.TabIndex = 0;
            btnConnect.Text = "连接";
            btnConnect.UseVisualStyleBackColor = true;
            btnConnect.Click += btnConnect_Click;
            // 
            // btnSettings
            // 
            btnSettings.Anchor = AnchorStyles.None;
            btnSettings.Font = new Font(UiDesignTokens.FontFamily, 14F);
            btnSettings.Location = new Point(382, 264);
            btnSettings.Name = "btnSettings";
            btnSettings.Size = new Size(200, 50);
            btnSettings.TabIndex = 1;
            btnSettings.Text = "设置";
            btnSettings.UseVisualStyleBackColor = true;
            btnSettings.Click += btnSettings_Click;
            // 
            // panelConnect
            // 
            panelConnect.Controls.Add(btnCancel);
            panelConnect.Controls.Add(lbConnecting);
            panelConnect.Dock = DockStyle.Fill;
            panelConnect.Location = new Point(0, 0);
            panelConnect.Name = "panelConnect";
            panelConnect.BackColor = UiDesignTokens.PageBackground;
            lbConnecting.ForeColor = UiDesignTokens.TextPrimary;
            panelConnect.Size = new Size(964, 511);
            panelConnect.TabIndex = 1;
            panelConnect.Visible = false;
            // 
            // btnCancel
            // 
            btnCancel.Anchor = AnchorStyles.None;
            btnCancel.Font = new Font(UiDesignTokens.FontFamily, 14F);
            btnCancel.Location = new Point(382, 275);
            btnCancel.Name = "btnCancel";
            btnCancel.Size = new Size(200, 50);
            btnCancel.TabIndex = 4;
            btnCancel.Text = "取消";
            btnCancel.UseVisualStyleBackColor = true;
            btnCancel.Visible = false;
            btnCancel.Click += btnCancel_Click;
            // 
            // lbConnecting
            // 
            lbConnecting.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            lbConnecting.Font = new Font(UiDesignTokens.FontFamily, 14.25F, FontStyle.Regular, GraphicsUnit.Point, 0);
            lbConnecting.Location = new Point(12, 208);
            lbConnecting.Name = "lbConnecting";
            lbConnecting.Size = new Size(940, 53);
            lbConnecting.TabIndex = 3;
            lbConnecting.Text = "正在连接…";
            lbConnecting.TextAlign = ContentAlignment.MiddleCenter;
            lbConnecting.Visible = false;
            // 
            // FormRdpClient
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(964, 511);
            Controls.Add(panelStartup);
            Controls.Add(panelConnect);
            Controls.Add(panelRdp);
            Name = "FormRdpClient";
            Text = "PureRDP";
            FormClosing += FormRdpClient_FormClosing;
            FormClosed += FormRdpClient_FormClosed;
            Shown += FormRdpClient_Shown;
            panelRdp.ResumeLayout(false);
            panelStartup.ResumeLayout(false);
            panelStartup.PerformLayout();
            panelConnect.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private Panel panelRdp;
        private Panel panelStartup;
        private TextBox lblStatusMessage;
        private Button btnConnect;
        private Button btnSettings;
        private Panel panelConnect;
        private Label lbConnecting;
        private Label lbRdp;
        private Button btnCancel;
    }
}
