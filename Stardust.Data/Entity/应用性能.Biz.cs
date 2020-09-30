﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.Serialization;
using System.Xml.Serialization;
using NewLife;
using NewLife.Caching;
using NewLife.Data;
using NewLife.Serialization;
using Stardust.Models;
using XCode;
using XCode.Membership;

namespace Stardust.Data
{
    /// <summary>应用性能。保存应用上报的性能数据，如CPU、内存、线程、句柄等</summary>
    public partial class AppMeter : EntityBase<AppMeter>
    {
        #region 对象操作
        static AppMeter()
        {
            // 累加字段，生成 Update xx Set Count=Count+1234 Where xxx
            //var df = Meta.Factory.AdditionalFields;
            //df.Add(nameof(AppId));

            // 过滤器 UserModule、TimeModule、IPModule
            Meta.Modules.Add<TimeModule>();
            Meta.Modules.Add<IPModule>();
        }

        /// <summary>验证并修补数据，通过抛出异常的方式提示验证失败。</summary>
        /// <param name="isNew">是否插入</param>
        public override void Valid(Boolean isNew)
        {
            // 如果没有脏数据，则不需要进行任何处理
            if (!HasDirty) return;

            // 建议先调用基类方法，基类方法会做一些统一处理
            base.Valid(isNew);

            // 在新插入数据或者修改了指定字段时进行修正
            //if (isNew && !Dirtys[nameof(CreateTime)]) CreateTime = DateTime.Now;
            //if (isNew && !Dirtys[nameof(CreateIP)]) CreateIP = ManageProvider.UserHost;
        }
        #endregion

        #region 扩展属性
        /// <summary>应用</summary>
        [XmlIgnore, IgnoreDataMember]
        //[ScriptIgnore]
        public App App => Extends.Get(nameof(App), k => App.FindByID(AppId));

        /// <summary>应用</summary>
        [XmlIgnore, IgnoreDataMember]
        //[ScriptIgnore]
        [DisplayName("应用")]
        [Map(nameof(AppId), typeof(App), "ID")]
        public String AppName => App?.Name;
        #endregion

        #region 扩展查询
        /// <summary>根据编号查找</summary>
        /// <param name="id">编号</param>
        /// <returns>实体对象</returns>
        public static AppMeter FindById(Int64 id)
        {
            if (id <= 0) return null;

            // 实体缓存
            if (Meta.Session.Count < 1000) return Meta.Cache.Find(e => e.Id == id);

            // 单对象缓存
            return Meta.SingleCache[id];

            //return Find(_.Id == id);
        }

        /// <summary>根据应用、编号查找</summary>
        /// <param name="appId">应用</param>
        /// <param name="id">编号</param>
        /// <returns>实体列表</returns>
        public static IList<AppMeter> FindAllByAppIdAndId(Int32 appId, Int64 id)
        {
            // 实体缓存
            if (Meta.Session.Count < 1000) return Meta.Cache.FindAll(e => e.AppId == appId && e.Id == id);

            return FindAll(_.AppId == appId & _.Id == id);
        }
        #endregion

        #region 高级查询
        /// <summary>高级查询</summary>
        /// <param name="appId">应用</param>
        /// <param name="clientId">客户端标识</param>
        /// <param name="start">开始时间</param>
        /// <param name="end">结束时间</param>
        /// <param name="key">关键字</param>
        /// <param name="page">分页参数信息。可携带统计和数据权限扩展查询等信息</param>
        /// <returns>实体列表</returns>
        public static IList<AppMeter> Search(Int32 appId, String clientId, DateTime start, DateTime end, String key, PageParameter page)
        {
            var exp = new WhereExpression();

            if (appId >= 0) exp &= _.AppId == appId;
            if (!clientId.IsNullOrEmpty() && clientId != "null") exp &= _.ClientId == clientId;
            exp &= _.Id.Between(start, end, Meta.Factory.Snow);
            if (!key.IsNullOrEmpty()) exp &= _.ClientId.Contains(key) | _.Data.Contains(key) | _.Creator.Contains(key) | _.CreateIP.Contains(key);

            return FindAll(exp, page);
        }

        private static readonly ICache _cache = new MemoryCache { Expire = 600 };
        /// <summary>获取某个应用下的客户端数</summary>
        /// <param name="appId"></param>
        /// <returns></returns>
        public static IDictionary<String, String> GetClientIds(Int32 appId)
        {
            var dic = new Dictionary<String, String>();
            if (appId <= 0) return dic;

            // TryGet能够扛缓存穿透，即使没有数据，也写入空数据
            var key = $"field:{appId}";
            if (_cache.TryGetValue<IDictionary<String, String>>(key, out var value)) return value;

            // 计算应用的ClientIds时，采取Id降序，较新活跃的客户端在前面
            var exp = new WhereExpression();
            exp &= _.AppId == appId & _.Id >= Meta.Factory.Snow.GetId(DateTime.Today);
            var list = FindAll(exp.GroupBy(_.ClientId), null, _.Id.Max() & _.ClientId);
            value = list.OrderByDescending(e => e.Id).ToDictionary(e => e.ClientId ?? "null", e => $"{e.ClientId}({e.Id})");

            _cache.Set(key, value);

            return value;
        }

        // Select Count(Id) as Id,Category From AppMeter Where CreateTime>'2020-01-24 00:00:00' Group By Category Order By Id Desc limit 20
        //static readonly FieldCache<AppMeter> _CategoryCache = new FieldCache<AppMeter>(nameof(Category))
        //{
        //Where = _.CreateTime > DateTime.Today.AddDays(-30) & Expression.Empty
        //};

        ///// <summary>获取类别列表，字段缓存10分钟，分组统计数据最多的前20种，用于魔方前台下拉选择</summary>
        ///// <returns></returns>
        //public static IDictionary<String, String> GetCategoryList() => _CategoryCache.FindAllName();
        #endregion

        #region 业务操作
        /// <summary>更新信息</summary>
        /// <param name="app"></param>
        /// <param name="info"></param>
        /// <param name="clientId"></param>
        public static void WriteData(App app, AppInfo info, String clientId)
        {
            // 插入节点数据
            var data = new AppMeter
            {
                AppId = app.ID,
                ClientId = clientId,
                Memory = (Int32)(info.WorkingSet / 1024 / 1024),
                ProcessorTime = info.ProcessorTime,
                Threads = info.Threads,
                Handles = info.Handles,
                Connections = info.Connections,

                Data = info.ToJson(),
                Creator = Environment.MachineName,
            };

            data.SaveAsync();
        }
        #endregion
    }
}