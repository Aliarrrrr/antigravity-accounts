mod hub_client;

use hub_client::HubClient;
use std::sync::Arc;
use tao::event::{Event, WindowEvent};
use tao::event_loop::{ControlFlow, EventLoopBuilder};
use tao::window::WindowBuilder;
use wry::WebViewBuilder;

#[derive(serde::Deserialize)]
struct IpcMessage {
    cmd: String,
    #[serde(default)]
    payload: serde_json::Value,
}

enum UserEvent {
    RunScript(String),
    ToggleGlass(bool),
}

fn build_bundled_html() -> String {
    let html = include_str!("../ui/index.html");
    let css = include_str!("../ui/liquid-glass.css");
    let js = include_str!("../ui/app.js");

    html.replace(
        r#"<link rel="stylesheet" href="liquid-glass.css">"#,
        &format!("<style>\n{}\n</style>", css),
    )
    .replace(
        r#"<script src="app.js"></script>"#,
        &format!("<script>\n{}\n</script>", js),
    )
}

fn send_js_event(proxy: &tao::event_loop::EventLoopProxy<UserEvent>, event_name: &str, data: &serde_json::Value) {
    let payload = serde_json::json!({
        "event": event_name,
        "data": data,
    });
    let script = format!("if (window.onRustMessage) {{ window.onRustMessage({}); }}", payload);
    let _ = proxy.send_event(UserEvent::RunScript(script));
}

fn log_to_js(proxy: &tao::event_loop::EventLoopProxy<UserEvent>, message: &str, level: &str) {
    let data = serde_json::json!({
        "message": message,
        "level": level,
    });
    send_js_event(proxy, "command_output", &data);
}

fn main() {
    let data_dir = std::env::var("LOCALAPPDATA")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|_| std::env::temp_dir())
        .join("AGProfileHubNext")
        .join("webview_data");
    let _ = std::fs::create_dir_all(&data_dir);
    std::env::set_var("WEBVIEW2_USER_DATA_FOLDER", &data_dir);

    eprintln!("[RUST-GUI] 正在启动主事件循环...");
    let event_loop = EventLoopBuilder::<UserEvent>::with_user_event().build();
    let proxy = event_loop.create_proxy();

    eprintln!("[RUST-GUI] 正在创建主窗口...");
    let window = WindowBuilder::new()
        .with_title("Antigravity Profile Hub Next · Rust GUI")
        .with_inner_size(tao::dpi::LogicalSize::new(1180.0, 780.0))
        .with_min_inner_size(tao::dpi::LogicalSize::new(920.0, 640.0))
        .with_visible(true)
        .with_decorations(true)
        .build(&event_loop)
        .expect("无法创建应用主窗口");

    #[cfg(target_os = "windows")]
    {
        let res_mica = window_vibrancy::apply_mica(&window, Some(true));
        eprintln!("[RUST-GUI] Windows 11 DWM Mica 应用结果: {:?}", res_mica);
    }

    let hub_client = Arc::new(HubClient::new());

    let ipc_proxy = proxy.clone();
    let client_for_ipc = Arc::clone(&hub_client);

    let html_content = build_bundled_html();

    let webview = WebViewBuilder::new()
        .with_html(&html_content)
        .with_ipc_handler(move |req| {
            let body = req.body();
            let msg: IpcMessage = match serde_json::from_str(body) {
                Ok(m) => m,
                Err(e) => {
                    eprintln!("无法解析 IPC 消息: {} (raw: {})", e, body);
                    return;
                }
            };

            let proxy = ipc_proxy.clone();
            let client_res = Arc::clone(&client_for_ipc);

            std::thread::spawn(move || {
                let client = match client_res.as_ref() {
                    Ok(c) => c,
                    Err(err) => {
                        log_to_js(&proxy, &format!("CLI 客户端未就绪: {}", err), "error");
                        return;
                    }
                };

                match msg.cmd.as_str() {
                    "set_liquid_glass" => {
                        let enabled = msg.payload.get("enabled").and_then(|v| v.as_bool()).unwrap_or(true);
                        let _ = proxy.send_event(UserEvent::ToggleGlass(enabled));
                        send_js_event(&proxy, "glass_mode_changed", &serde_json::json!({ "enabled": enabled }));
                    }
                    "status_all" => {
                        match client.status_all() {
                            Ok(profiles) => {
                                send_js_event(&proxy, "profiles_updated", &profiles);
                            }
                            Err(e) => {
                                log_to_js(&proxy, &format!("获取 Profile 列表失败: {}", e), "error");
                            }
                        }
                    }
                    "create_profile" => {
                        let name = msg.payload.get("name").and_then(|v| v.as_str()).unwrap_or("");
                        let ws = msg.payload.get("workspace").and_then(|v| v.as_str());
                        let bridge = msg.payload.get("bridge").and_then(|v| v.as_str()).unwrap_or("on");

                        log_to_js(&proxy, &format!("创建 Profile: {} (bridge: {})...", name, bridge), "info");
                        match client.create_profile(name, ws, bridge) {
                            Ok(profiles) => {
                                log_to_js(&proxy, &format!("Profile [{}] 创建成功！", name), "success");
                                send_js_event(&proxy, "profiles_updated", &profiles);
                            }
                            Err(e) => {
                                log_to_js(&proxy, &format!("创建失败: {}", e), "error");
                            }
                        }
                    }
                    "launch_profile" => {
                        let id = msg.payload.get("id").and_then(|v| v.as_str()).unwrap_or("");
                        log_to_js(&proxy, &format!("正在启动实例: {}...", id), "info");
                        match client.launch_profile(id) {
                            Ok(out) => {
                                log_to_js(&proxy, &format!("启动返回: {}", out.trim()), "success");
                                if let Ok(profiles) = client.status_all() {
                                    send_js_event(&proxy, "profiles_updated", &profiles);
                                }
                            }
                            Err(e) => log_to_js(&proxy, &format!("启动失败: {}", e), "error"),
                        }
                    }
                    "stop_profile" => {
                        let id = msg.payload.get("id").and_then(|v| v.as_str()).unwrap_or("");
                        let force = msg.payload.get("force").and_then(|v| v.as_bool()).unwrap_or(false);
                        log_to_js(&proxy, &format!("正在关闭实例: {} (force: {})...", id, force), "info");
                        match client.stop_profile(id, force) {
                            Ok(out) => {
                                log_to_js(&proxy, &format!("关闭完成: {}", out.trim()), "success");
                                if let Ok(profiles) = client.status_all() {
                                    send_js_event(&proxy, "profiles_updated", &profiles);
                                }
                            }
                            Err(e) => log_to_js(&proxy, &format!("关闭失败: {}", e), "error"),
                        }
                    }
                    "begin_login" => {
                        let id = msg.payload.get("id").and_then(|v| v.as_str()).unwrap_or("");
                        log_to_js(&proxy, &format!("实例 {} 申请授权槽位...", id), "info");
                        match client.begin_login(id) {
                            Ok(_) => {
                                log_to_js(&proxy, "已获得授权槽位。请在官方窗口完成登录。", "success");
                                if let Ok(profiles) = client.status_all() {
                                    send_js_event(&proxy, "profiles_updated", &profiles);
                                }
                            }
                            Err(e) => log_to_js(&proxy, &format!("申请授权失败: {}", e), "error"),
                        }
                    }
                    "cancel_login" => {
                        let id = msg.payload.get("id").and_then(|v| v.as_str()).unwrap_or("");
                        match client.cancel_login(id) {
                            Ok(_) => {
                                log_to_js(&proxy, "已取消授权等待。", "info");
                                if let Ok(profiles) = client.status_all() {
                                    send_js_event(&proxy, "profiles_updated", &profiles);
                                }
                            }
                            Err(e) => log_to_js(&proxy, &format!("取消失败: {}", e), "error"),
                        }
                    }
                    "rename_profile" => {
                        let id = msg.payload.get("id").and_then(|v| v.as_str()).unwrap_or("");
                        let name = msg.payload.get("name").and_then(|v| v.as_str()).unwrap_or("");
                        match client.rename_profile(id, name) {
                            Ok(_) => {
                                log_to_js(&proxy, &format!("已更名为 [{}]", name), "success");
                                if let Ok(profiles) = client.status_all() {
                                    send_js_event(&proxy, "profiles_updated", &profiles);
                                }
                            }
                            Err(e) => log_to_js(&proxy, &format!("重命名失败: {}", e), "error"),
                        }
                    }
                    "set_workspace" => {
                        let id = msg.payload.get("id").and_then(|v| v.as_str()).unwrap_or("");
                        let path = msg.payload.get("path").and_then(|v| v.as_str()).unwrap_or("");
                        match client.set_workspace(id, path) {
                            Ok(_) => {
                                log_to_js(&proxy, &format!("已绑定 Workspace 路径: {}", path), "success");
                                if let Ok(profiles) = client.status_all() {
                                    send_js_event(&proxy, "profiles_updated", &profiles);
                                }
                            }
                            Err(e) => log_to_js(&proxy, &format!("设置路径失败: {}", e), "error"),
                        }
                    }
                    "probe" => {
                        log_to_js(&proxy, "正在独立临时 Profile 中探测，最长约两分钟...", "info");
                        match client.probe() {
                            Ok(out) => log_to_js(&proxy, &format!("探测结果:\n{}", out), "success"),
                            Err(e) => log_to_js(&proxy, &format!("探测失败: {}", e), "error"),
                        }
                    }
                    "doctor" => {
                        log_to_js(&proxy, "正在执行完整环境诊断...", "info");
                        match client.doctor() {
                            Ok(out) => log_to_js(&proxy, &format!("诊断结果:\n{}", out), "success"),
                            Err(e) => log_to_js(&proxy, &format!("诊断失败: {}", e), "error"),
                        }
                    }
                    "register_protocol" => {
                        match client.register_protocol() {
                            Ok(_) => log_to_js(&proxy, "已注册协议回调！", "success"),
                            Err(e) => log_to_js(&proxy, &format!("注册回调失败: {}", e), "error"),
                        }
                    }
                    "shortcut" => {
                        let id = msg.payload.get("id").and_then(|v| v.as_str()).unwrap_or("");
                        match client.shortcut(id) {
                            Ok(out) => log_to_js(&proxy, &format!("快捷方式已创建: {}", out), "success"),
                            Err(e) => log_to_js(&proxy, &format!("创建快捷方式失败: {}", e), "error"),
                        }
                    }
                    "open_logs" => {
                        let _ = client.open_logs();
                        log_to_js(&proxy, "已在资源管理器中打开日志目录。", "info");
                    }
                    other => {
                        log_to_js(&proxy, &format!("未实现指令: {}", other), "warn");
                    }
                }
            });
        })
        .build(&window)
        .unwrap_or_else(|e| {
            eprintln!("[RUST-GUI ERROR] WebView 初始化失败: {:?}", e);
            std::process::exit(1);
        });
    window.set_visible(true);
    window.set_focus();

    #[cfg(target_os = "windows")]
    {
        use tao::platform::windows::WindowExtWindows;
        let hwnd = window.hwnd();
        eprintln!("[RUST-GUI] HWND 具体数值: 0x{:X}", hwnd);
        unsafe {
            windows_sys::Win32::UI::WindowsAndMessaging::ShowWindow(
                hwnd as _,
                windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOW,
            );
            windows_sys::Win32::UI::WindowsAndMessaging::SetForegroundWindow(hwnd as _);
        }
    }

    eprintln!("[RUST-GUI] 窗口已强制设为可见并置顶，进入事件循环...");

    event_loop.run(move |event, _, control_flow| {
        *control_flow = ControlFlow::Wait;
        match event {
            Event::WindowEvent {
                event: WindowEvent::CloseRequested,
                ..
            } => {
                *control_flow = ControlFlow::Exit;
            }
            Event::UserEvent(UserEvent::RunScript(script)) => {
                let _ = webview.evaluate_script(&script);
            }
            Event::UserEvent(UserEvent::ToggleGlass(enabled)) => {
                #[cfg(target_os = "windows")]
                {
                    if enabled {
                        let _ = window_vibrancy::apply_mica(&window, Some(true));
                    } else {
                        let _ = window_vibrancy::clear_mica(&window);
                    }
                }
            }
            _ => {}
        }
    });
}
