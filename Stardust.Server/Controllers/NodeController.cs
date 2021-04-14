﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using NewLife;
using NewLife.Caching;
using NewLife.Log;
using NewLife.Remoting;
using NewLife.Security;
using NewLife.Serialization;
using NewLife.Web;
using Stardust.Data.Nodes;
using Stardust.Models;
using Stardust.Server.Common;
using Stardust.Server.Services;
using XCode;
using IActionFilter = Microsoft.AspNetCore.Mvc.Filters.IActionFilter;

namespace Stardust.Server.Controllers
{
    [ApiController]
    [Route("[controller]")]
    [ApiFilter]
    public class NodeController : ControllerBase, IActionFilter
    {
        /// <summary>用户主机</summary>
        public String UserHost => HttpContext.GetUserHost();

        /// <summary>节点引用，令牌无效时使用</summary>
        protected Node _nodeForHistory;

        private readonly ICache _cache;

        public NodeController(ICache cache) => _cache = cache;

        void IActionFilter.OnActionExecuting(ActionExecutingContext context) { }

        /// <summary>请求处理后</summary>
        /// <param name="context"></param>
        public void OnActionExecuted(ActionExecutedContext context)
        {
            if (context.Exception != null)
            {
                // 拦截全局异常，写日志
                var action = context.HttpContext.Request.Path + "";
                if (context.ActionDescriptor is ControllerActionDescriptor act) action = $"{act.ControllerName}/{act.ActionName}";

                WriteHistory(_nodeForHistory, action, false, context.Exception?.GetTrue() + "");
            }
        }

        #region 登录
        [HttpPost(nameof(Login))]
        public LoginResponse Login(LoginInfo inf)
        {
            var code = inf.Code;
            var secret = inf.Secret;

            var node = Node.FindByCode(code, true);
            var di = inf.Node;
            _nodeForHistory = node;

            // 校验唯一编码，防止客户端拷贝配置
            var autoReg = false;
            if (node == null)
            {
                node = AutoRegister(null, inf, out autoReg);
            }
            else
            {
                if (!node.Enable) throw new ApiException(99, "禁止登录");
                node = CheckNode(node, di);

                // 登录密码未设置或者未提交，则执行动态注册
                if (node.Secret.IsNullOrEmpty() || secret.IsNullOrEmpty())
                    node = AutoRegister(node, inf, out autoReg);
                else if (node.Secret.MD5() != secret)
                    node = AutoRegister(node, inf, out autoReg);
            }

            _nodeForHistory = node ?? throw new ApiException(12, "节点鉴权失败");

            node.Login(di, UserHost);

            // 设置令牌
            var tm = IssueToken(node.Code, Setting.Current);

            // 在线记录
            var olt = GetOnline(node) ?? CreateOnline(node, tm.AccessToken);
            olt.Save(di, null, tm.AccessToken);

            // 登录历史
            WriteHistory(node, "节点鉴权", true, $"[{node.Name}/{node.Code}]鉴权成功 " + di.ToJson(false, false, false));

            var rs = new LoginResponse
            {
                Name = node.Name,
                Token = tm.AccessToken,
            };

            // 动态注册，下发节点证书
            if (autoReg)
            {
                rs.Code = node.Code;
                rs.Secret = node.Secret;
            }

            return rs;
        }

        /// <summary>注销</summary>
        /// <param name="reason">注销原因</param>
        /// <param name="token">令牌</param>
        /// <returns></returns>
        [HttpGet(nameof(Logout))]
        [HttpPost(nameof(Logout))]
        public LoginResponse Logout(String reason, String token)
        {
            var node = DecodeToken(token, Setting.Current.TokenSecret);
            if (node != null)
            {
                var olt = GetOnline(node);
                if (olt != null)
                {
                    var msg = $"{reason} [{node}]]登录于{olt.CreateTime}，最后活跃于{olt.UpdateTime}";
                    WriteHistory(node, "节点下线", true, msg);
                    olt.Delete();

                    var sid = $"{node.ID}@{UserHost}";
                    _cache.Remove($"NodeOnline:{sid}");

                    // 计算在线时长
                    if (olt.CreateTime.Year > 2000)
                    {
                        node.OnlineTime += (Int32)(DateTime.Now - olt.CreateTime).TotalSeconds;
                        node.SaveAsync();
                    }

                    NodeOnlineService.CheckOffline(node, "注销");
                }
            }

            return new LoginResponse
            {
                Name = node?.Name,
                Token = null,
            };
        }

        /// <summary>
        /// 校验节点密钥
        /// </summary>
        /// <param name="node"></param>
        /// <param name="ps"></param>
        /// <returns></returns>
        private Node CheckNode(Node node, NodeInfo di)
        {
            // 校验唯一编码，防止客户端拷贝配置
            var uuid = di.UUID;
            var guid = di.MachineGuid;
            var diskid = di.DiskID;
            if (!uuid.IsNullOrEmpty() && uuid != node.Uuid)
            {
                WriteHistory(node, "登录校验", false, $"唯一标识不符！{uuid}!={node.Uuid}");
                return null;
            }
            if (!guid.IsNullOrEmpty() && guid != node.MachineGuid)
            {
                WriteHistory(node, "登录校验", false, $"机器标识不符！{guid}!={node.MachineGuid}");
                return null;
            }
            if (!diskid.IsNullOrEmpty() && diskid != node.DiskID)
            {
                WriteHistory(node, "登录校验", false, $"磁盘序列号不符！{diskid}!={node.DiskID}");
                return null;
            }

            // 机器名
            if (di.MachineName != node.MachineName)
            {
                WriteHistory(node, "登录校验", false, $"机器名不符！{di.MachineName}!={node.MachineName}");
            }

            // 网卡地址
            if (di.Macs != node.MACs)
            {
                var dims = di.Macs?.Split(",") ?? new String[0];
                var nodems = node.MACs?.Split(",") ?? new String[0];
                // 任意网卡匹配则通过
                if (!nodems.Any(e => dims.Contains(e)))
                {
                    WriteHistory(node, "登录校验", false, $"网卡地址不符！{di.Macs}!={node.MACs}");
                }
            }

            return node;
        }

        private Node AutoRegister(Node node, LoginInfo inf, out Boolean autoReg)
        {
            var set = Setting.Current;
            if (!set.AutoRegister) throw new ApiException(12, "禁止自动注册");

            var di = inf.Node;
            if (node == null)
            {
                // 该硬件的所有节点信息
                var list = Node.Search(di.UUID, di.MachineGuid, di.Macs);

                // 当前节点信息，取较老者
                list = list.OrderBy(e => e.ID).ToList();

                // 找到节点
                if (node == null) node = list.FirstOrDefault();
            }

            var ip = UserHost;
            var name = "";
            if (name.IsNullOrEmpty()) name = di.MachineName;
            if (name.IsNullOrEmpty()) name = di.UserName;

            if (node == null) node = new Node
            {
                Enable = true,

                CreateIP = ip,
                CreateTime = DateTime.Now,
            };

            // 如果未打开动态注册，则把节点修改为禁用
            node.Enable = set.AutoRegister;

            if (node.Name.IsNullOrEmpty()) node.Name = name;

            // 优先使用节点散列来生成节点证书，确保节点路由到其它接入网关时保持相同证书代码
            node.Code = BuildCode(di);
            if (node.Code.IsNullOrEmpty()) node.Code = Rand.NextString(8);

            node.Secret = Rand.NextString(16);
            node.UpdateIP = ip;
            node.UpdateTime = DateTime.Now;

            node.Save();
            autoReg = true;

            WriteHistory(node, "动态注册", true, inf.ToJson(false, false, false));

            return node;
        }

        private String BuildCode(NodeInfo di)
        {
            var set = Setting.Current;
            //var uid = $"{di.UUID}@{di.MachineGuid}@{di.Macs}";
            var ss = (set.NodeCodeFormula + "").Split(new[] { '(', ')' });
            if (ss.Length >= 2)
            {
                var uid = ss[1];
                foreach (var pi in di.GetType().GetProperties())
                {
                    uid = uid.Replace($"{{{pi.Name}}}", pi.GetValue(di) + "");
                }
                if (!uid.IsNullOrEmpty())
                {
                    // 使用产品类别加密一下，确保不同类别有不同编码
                    var buf = uid.GetBytes();
                    //code = buf.Crc().GetBytes().ToHex();
                    switch (ss[0].ToLower())
                    {
                        case "crc": buf = buf.Crc().GetBytes(); break;
                        case "crc16": buf = buf.Crc16().GetBytes(); break;
                        case "md5": buf = buf.MD5(); break;
                        case "md5_16": buf = uid.MD5_16().ToHex(); break;
                        default:
                            break;
                    }
                    return buf.ToHex();
                }
            }

            return null;
        }
        #endregion

        #region 心跳
        //[HttpGet(nameof(Ping))]
        [HttpPost(nameof(Ping))]
        public PingResponse Ping(PingInfo inf, String token)
        {
            var rs = new PingResponse
            {
                Time = inf.Time,
                ServerTime = DateTime.UtcNow,
            };

            var node = DecodeToken(token, Setting.Current.TokenSecret);
            if (node != null)
            {
                node.FixArea();
                node.SaveAsync();

                rs.Period = node.Period;

                var olt = GetOnline(node) ?? CreateOnline(node, token);
                olt.Name = node.Name;
                olt.Category = node.Category;
                olt.Version = node.Version;
                olt.CompileTime = node.CompileTime;
                olt.Save(null, inf, token);

                // 拉取命令
                rs.Commands = AcquireCommands(node.ID);
            }

            return rs;
        }

        private static IList<NodeCommand> _commands;
        private static DateTime _nextTime;

        private static CommandModel[] AcquireCommands(Int32 nodeId)
        {
            // 缓存最近1000个未执行命令，用于快速过滤，避免大量节点在线时频繁查询命令表
            if (_nextTime < DateTime.Now)
            {
                _commands = NodeCommand.AcquireCommands(-1, 1000);
                _nextTime = DateTime.Now.AddMinutes(1);
            }

            // 是否有本节点
            if (!_commands.Any(e => e.NodeID == nodeId)) return null;

            var cmds = NodeCommand.AcquireCommands(nodeId, 100);
            if (cmds == null) return null;

            var rs = cmds.Select(e => new CommandModel
            {
                Id = e.ID,
                Command = e.Command,
                Argument = e.Argument,
                Expire = e.Expire,
            }).ToArray();

            foreach (var item in cmds)
            {
                item.Finished = true;
                item.UpdateTime = DateTime.Now;
            }
            cmds.Update(false);

            return rs;
        }

        //[TokenFilter]
        [HttpGet(nameof(Ping))]
        public PingResponse Ping()
        {
            return new PingResponse
            {
                Time = 0,
                ServerTime = DateTime.Now,
            };
        }

        /// <summary></summary>
        /// <param name="node"></param>
        /// <returns></returns>
        protected virtual NodeOnline GetOnline(Node node)
        {
            var sid = $"{node.ID}@{UserHost}";
            var olt = _cache.Get<NodeOnline>($"NodeOnline:{sid}");
            if (olt != null) return olt;

            return NodeOnline.FindBySessionID(sid);
        }

        /// <summary>检查在线</summary>
        /// <param name="node"></param>
        /// <returns></returns>
        protected virtual NodeOnline CreateOnline(Node node, String token)
        {
            var sid = $"{node.ID}@{UserHost}";
            var olt = NodeOnline.GetOrAdd(sid);
            olt.NodeID = node.ID;
            olt.Name = node.Name;
            olt.IP = node.IP;
            olt.Category = node.Category;
            olt.ProvinceID = node.ProvinceID;
            olt.CityID = node.CityID;

            olt.Version = node.Version;
            olt.CompileTime = node.CompileTime;
            olt.Memory = node.Memory;
            olt.MACs = node.MACs;
            //olt.COMs = node.COMs;
            olt.Token = token;
            olt.CreateIP = UserHost;

            olt.Creator = Environment.MachineName;

            _cache.Set($"NodeOnline:{sid}", olt, 600);

            return olt;
        }
        #endregion

        #region 历史
        /// <summary>上报数据，针对命令</summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpPost(nameof(Report))]
        public async Task<Object> Report(Int32 id, String token)
        {
            var node = DecodeToken(token, Setting.Current.TokenSecret);
            if (node == null) throw new ApiException(402, "节点未登录");

            var cmd = NodeCommand.FindByID(id);
            if (cmd != null && cmd.NodeID == node.ID)
            {
                var ms = Request.Body;
                if (Request.ContentLength > 0)
                {
                    var rs = "";
                    switch (cmd.Command)
                    {
                        case "截屏":
                            rs = await SaveFileAsync(cmd, ms, "png");
                            break;
                        case "抓日志":
                            rs = await SaveFileAsync(cmd, ms, "log");
                            break;
                        default:
                            rs = await SaveFileAsync(cmd, ms, "bin");
                            break;
                    }

                    if (!rs.IsNullOrEmpty())
                    {
                        cmd.Result = rs;
                        cmd.Save();

                        WriteHistory(node, cmd.Command, true, rs);
                    }
                }
            }

            return null;
        }

        private async Task<String> SaveFileAsync(NodeCommand cmd, Stream ms, String ext)
        {
            var file = $"../{cmd.Command}/{DateTime.Today:yyyyMMdd}/{cmd.NodeID}_{cmd.ID}.{ext}";
            file.EnsureDirectory(true);

            using var fs = file.AsFile().OpenWrite();
            await ms.CopyToAsync(fs);
            await ms.FlushAsync();

            return file;
        }
        #endregion

        #region 升级
        /// <summary>升级检查</summary>
        /// <param name="channel">更新通道</param>
        /// <returns></returns>
        [HttpGet(nameof(Upgrade))]
        public UpgradeInfo Upgrade(String channel, String token)
        {
            var node = DecodeToken(token, Setting.Current.TokenSecret);
            if (node == null) throw new ApiException(402, "节点未登录");

            // 默认Release通道
            if (!Enum.TryParse<NodeChannels>(channel, true, out var ch)) ch = NodeChannels.Release;
            if (ch < NodeChannels.Release) ch = NodeChannels.Release;

            // 找到所有产品版本
            var list = NodeVersion.GetValids(ch);

            // 应用过滤规则，使用最新的一个版本
            var pv = list.Where(e => e.Match(node)).OrderByDescending(e => e.Version).FirstOrDefault();
            if (pv == null) return null;
            //if (pv == null) throw new ApiException(509, "没有升级规则");

            WriteHistory(node, "自动更新", true, $"channel={ch} => [{pv.ID}] {pv.Version} {pv.Source} {pv.Executor}");

            return new UpgradeInfo
            {
                Version = pv.Version,
                Source = pv.Source,
                Executor = pv.Executor,
                Force = pv.Force,
                Description = pv.Description,
            };
        }
        #endregion

        #region 下行通知
        /// <summary>下行通知</summary>
        /// <returns></returns>
        [HttpGet("/node/notify")]
        public async Task Notify()
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                var token = (HttpContext.Request.Headers["Authorization"] + "").TrimStart("Bearer ");
                var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                var source = new CancellationTokenSource();
                try
                {
                    await Handle(webSocket, token, source.Token);
                }
                catch (Exception ex)
                {
                    source.Cancel();
                    await webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, ex.Message, default);
                }
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
            }
        }

        private async Task Handle(WebSocket websocket, String token, CancellationToken cancellationToken)
        {
            var node = DecodeToken(token, Setting.Current.TokenSecret);
            if (node == null) throw new InvalidOperationException("未登录！");

            XTrace.WriteLine("websocket连接/node_ws {0}", node);

            var queue = _cache.GetQueue<String>($"cmd:{node.Code}");
            while (!cancellationToken.IsCancellationRequested && websocket.State == WebSocketState.Open)
            {
                var msg = await queue.TakeOneAsync(10_000);
                if (msg != null)
                {
                    await websocket.SendAsync(msg.GetBytes(), WebSocketMessageType.Text, true, cancellationToken);
                }
                else
                {
                    // 后续MemoryQueue升级到异步阻塞版以后，这里可以缩小
                    await Task.Delay(1_000);
                }
            }

            XTrace.WriteLine("websocket关闭/node_ws {0}", node);

            await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "finish", cancellationToken);
        }
        #endregion

        #region 辅助
        private TokenModel IssueToken(String name, Setting set)
        {
            // 颁发令牌
            var ss = set.TokenSecret.Split(':');
            var jwt = new JwtBuilder
            {
                Issuer = Assembly.GetEntryAssembly().GetName().Name,
                Subject = name,
                Id = Rand.NextString(8),
                Expire = DateTime.Now.AddSeconds(set.TokenExpire),

                Algorithm = ss[0],
                Secret = ss[1],
            };

            return new TokenModel
            {
                AccessToken = jwt.Encode(null),
                TokenType = jwt.Type ?? "JWT",
                ExpireIn = set.TokenExpire,
                RefreshToken = jwt.Encode(null),
            };
        }

        private Node DecodeToken(String token, String tokenSecret)
        {
            //if (token.IsNullOrEmpty()) throw new ArgumentNullException(nameof(token));
            if (token.IsNullOrEmpty()) throw new ApiException(402, "节点未登录");

            // 解码令牌
            var ss = tokenSecret.Split(':');
            var jwt = new JwtBuilder
            {
                Algorithm = ss[0],
                Secret = ss[1],
            };

            var rs = jwt.TryDecode(token, out var message);
            var node = Node.FindByCode(jwt.Subject);
            _nodeForHistory = node;
            if (!rs) throw new ApiException(403, $"非法访问 {message}");

            return node;
        }

        private void WriteHistory(Node node, String action, Boolean success, String remark) => NodeHistory.Create(node, action, success, remark, Environment.MachineName, UserHost);
        #endregion
    }
}