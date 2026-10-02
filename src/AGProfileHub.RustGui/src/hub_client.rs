use std::path::PathBuf;
use std::process::Command;

pub struct HubClient {
    pub cli_path: PathBuf,
    pub dotnet_root: Option<PathBuf>,
}

impl HubClient {
    pub fn new() -> Result<Self, String> {
        let (cli_path, dotnet_root) = Self::locate_dependencies()?;
        Ok(Self {
            cli_path,
            dotnet_root,
        })
    }

    fn locate_dependencies() -> Result<(PathBuf, Option<PathBuf>), String> {
        let current_dir = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."));
        let exe_dir = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|p| p.to_path_buf()))
            .unwrap_or_else(|| current_dir.clone());

        // 探测 agprofile-next.exe
        let cli_candidates = [
            // 本地开发目录
            current_dir.join("src/AGProfileHub.Cli/bin/Release/net10.0/agprofile-next.exe"),
            current_dir.join("src/AGProfileHub.Cli/bin/Debug/net10.0/agprofile-next.exe"),
            current_dir.join("../AGProfileHub.Cli/bin/Release/net10.0/agprofile-next.exe"),
            current_dir.join("../../AGProfileHub.Cli/bin/Release/net10.0/agprofile-next.exe"),
            // 发布包目录
            exe_dir.join("agprofile-next.exe"),
            exe_dir.join("../cli/agprofile-next.exe"),
            exe_dir.join("../../cli/agprofile-next.exe"),
            exe_dir.join("../../../AGProfileHub.Cli/bin/Release/net10.0/agprofile-next.exe"),
        ];

        let mut found_cli = None;
        for c in &cli_candidates {
            if c.is_file() {
                if let Ok(canon) = c.canonicalize() {
                    found_cli = Some(canon);
                    break;
                }
            }
        }

        let cli_path = found_cli.ok_or_else(|| {
            "无法定位 agprofile-next.exe。请确保已编译 CLI 项目。".to_string()
        })?;

        // 探测 .tools/dotnet 本地 SDK/Runtime
        let dotnet_candidates = [
            current_dir.join(".tools/dotnet"),
            current_dir.join("../.tools/dotnet"),
            current_dir.join("../../.tools/dotnet"),
            exe_dir.join(".tools/dotnet"),
            exe_dir.join("../../.tools/dotnet"),
            exe_dir.join("../../../.tools/dotnet"),
        ];

        let mut found_dotnet = None;
        for d in &dotnet_candidates {
            if d.join("dotnet.exe").is_file() {
                if let Ok(canon) = d.canonicalize() {
                    found_dotnet = Some(canon);
                    break;
                }
            }
        }

        Ok((cli_path, found_dotnet))
    }

    pub fn run(&self, args: &[&str]) -> Result<String, String> {
        let mut cmd = Command::new(&self.cli_path);
        cmd.args(args);

        if let Some(ref dotnet) = self.dotnet_root {
            cmd.env("DOTNET_ROOT", dotnet);
            // 确保 dotnet 优先处于 PATH
            if let Some(existing_path) = std::env::var_os("PATH") {
                let mut new_path = dotnet.as_os_str().to_os_string();
                new_path.push(";");
                new_path.push(existing_path);
                cmd.env("PATH", new_path);
            }
        }

        let output = cmd.output().map_err(|e| format!("执行 CLI 失败: {}", e))?;

        let stdout = String::from_utf8_lossy(&output.stdout).to_string();
        let stderr = String::from_utf8_lossy(&output.stderr).to_string();

        if !output.status.success() {
            let msg = if !stderr.trim().is_empty() {
                stderr.trim().to_string()
            } else if !stdout.trim().is_empty() {
                stdout.trim().to_string()
            } else {
                format!("进程退出码: {:?}", output.status.code())
            };
            return Err(msg);
        }

        Ok(stdout)
    }

    pub fn status_all(&self) -> Result<serde_json::Value, String> {
        let raw = self.run(&["ls", "--json"])?;
        serde_json::from_str(&raw).map_err(|e| format!("解析 Profile 状态 JSON 失败: {}", e))
    }

    pub fn create_profile(&self, name: &str, workspace: Option<&str>, bridge: &str) -> Result<serde_json::Value, String> {
        let mut args = vec!["new", name];
        if let Some(ws) = workspace {
            args.push(ws);
        }
        let _ = self.run(&args)?;
        if bridge == "on" || bridge == "off" {
            let _ = self.run(&["bridge", name, bridge])?;
        }
        self.status_all()
    }

    pub fn launch_profile(&self, id: &str) -> Result<String, String> {
        self.run(&["open", id])
    }

    pub fn stop_profile(&self, id: &str, force: bool) -> Result<String, String> {
        if force {
            self.run(&["close", id, "--force"])
        } else {
            self.run(&["close", id])
        }
    }

    pub fn begin_login(&self, id: &str) -> Result<String, String> {
        self.run(&["begin-login", id])
    }

    pub fn cancel_login(&self, id: &str) -> Result<String, String> {
        self.run(&["cancel-login", id])
    }

    pub fn rename_profile(&self, id: &str, new_name: &str) -> Result<String, String> {
        self.run(&["rename", id, new_name])
    }

    pub fn set_workspace(&self, id: &str, path: &str) -> Result<String, String> {
        self.run(&["workspace", id, path])
    }

    pub fn probe(&self) -> Result<String, String> {
        self.run(&["probe"])
    }

    pub fn doctor(&self) -> Result<String, String> {
        self.run(&["doctor"])
    }

    pub fn register_protocol(&self) -> Result<String, String> {
        self.run(&["configure-deeplink", "register"])
    }

    pub fn shortcut(&self, id: &str) -> Result<String, String> {
        self.run(&["shortcut", id])
    }

    pub fn open_logs(&self) -> Result<(), String> {
        let _ = self.run(&["open-logs"]);
        Ok(())
    }
}
