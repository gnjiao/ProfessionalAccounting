﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AccountingServer.BLL;
using AccountingServer.BLL.Parsing;
using AccountingServer.BLL.Util;
using AccountingServer.Entities;
using AccountingServer.Entities.Util;
using AccountingServer.Shell.Serializer;
using AccountingServer.Shell.Util;
using CredentialManagement;

namespace AccountingServer.Shell.Plugins.THUInfo
{
    /// <summary>
    ///     从info.tsinghua.edu.cn更新账户
    /// </summary>
    public partial class THUInfo : PluginBase
    {
        /// <summary>
        ///     对账忽略标志
        /// </summary>
        private const string IgnoranceMark = "reconciliation";

        private static readonly IQueryCompunded<IDetailQueryAtom> DetailQuery =
            new DetailQueryAryBase(
                OperatorType.Substract,
                new IQueryCompunded<IDetailQueryAtom>[]
                    {
                        new DetailQueryAtomBase(new VoucherDetail { Title = 1012, SubTitle = 05 }),
                        new DetailQueryAryBase(
                            OperatorType.Union,
                            new IQueryCompunded<IDetailQueryAtom>[]
                                {
                                    new DetailQueryAtomBase(new VoucherDetail { Remark = "" }),
                                    new DetailQueryAtomBase(new VoucherDetail { Remark = IgnoranceMark })
                                }
                        )
                    });

        private static readonly ConfigManager<EndPointTemplates> EndPointTemplates;

        private static IReadOnlyList<EndPointTemplate> Templates => EndPointTemplates.Config.Templates.AsReadOnly();

        private readonly Crawler m_Crawler;

        private readonly object m_Lock = new object();

        static THUInfo() { EndPointTemplates = new ConfigManager<EndPointTemplates>("EndPoint.xml"); }

        public THUInfo(Accountant accountant, IEntitySerializer serializer) : base(accountant, serializer)
        {
            m_Crawler = new Crawler();
            Task.Run(() => FetchData());
        }

        /// <summary>
        ///     读取凭证并获取数据
        /// </summary>
        private void FetchData()
        {
            var cred = CredentialTemplate();
            if (cred.Exists())
                cred.Load();
            else
            {
                cred = PromptForCredential();
                if (cred == null)
                    return;
            }

            lock (m_Lock)
                m_Crawler.FetchData(cred.Username, cred.Password);
        }

        private static Credential CredentialTemplate() =>
            new Credential
                {
                    Target = "THUInfo",
                    PersistanceType = PersistanceType.Enterprise,
                    Type = CredentialType.DomainVisiblePassword
                };

        /// <summary>
        ///     删除保存的凭证
        /// </summary>
        private static void DropCredential()
        {
            var cred = CredentialTemplate();
            cred.Delete();
        }

        /// <summary>
        ///     提示输入凭证
        /// </summary>
        /// <returns>凭证</returns>
        private static Credential PromptForCredential()
        {
            var prompt = new XPPrompt
                {
                    Target = "THUInfo",
                    Persist = true
                };
            if (prompt.ShowDialog() != DialogResult.OK)
                return null;

            var cred = CredentialTemplate();
            cred.Username = prompt.Username.Split(new[] { '\\' }, 2)[1];
            cred.Password = prompt.Password;

            cred.Save();
            return cred;
        }

        /// <inheritdoc />
        public override IQueryResult Execute(string expr)
        {
            if (FacadeF.ParsingF.Optional(ref expr, "ep"))
            {
                FacadeF.ParsingF.Eof(expr);
                return ShowEndPoints();
            }

            if (FacadeF.ParsingF.Optional(ref expr, "cred"))
            {
                FacadeF.ParsingF.Eof(expr);
                DropCredential();
                FetchData();
            }

            Problems problems;
            lock (m_Lock)
                problems = Compare();

            if (FacadeF.ParsingF.Optional(ref expr, "whatif"))
            {
                FacadeF.ParsingF.Eof(expr);
                return ShowComparison(problems);
            }

            if (problems.Any)
                return ShowComparison(problems);

            if (!problems.Records.Any())
                return new Succeed();

            var pars = new List<string>();
            while (!string.IsNullOrWhiteSpace(expr))
                pars.Add(FacadeF.ParsingF.Token(ref expr));

            List<TransactionRecord> fail;
            foreach (var voucher in AutoGenerate(problems.Records, pars, out fail))
                Accountant.Upsert(voucher);

            if (!fail.Any())
                return new Succeed();

            var sb = new StringBuilder();
            sb.AppendLine("---Can not generate");
            foreach (var r in fail)
                sb.AppendLine(r.ToString());

            return new EditableText(sb.ToString());
        }

        /// <summary>
        ///     列出终端列表
        /// </summary>
        /// <returns>执行结果</returns>
        private IQueryResult ShowEndPoints()
        {
            var sb = new StringBuilder();
            var voucherQuery = new VoucherQueryAtomBase { DetailFilter = DetailQuery };
            var bin = new HashSet<int>();
            foreach (var d in Accountant.SelectVouchers(voucherQuery)
                .SelectMany(
                    v => v.Details.Where(d => d.IsMatch(DetailQuery))
                        .Select(d => new VDetail { Detail = d, Voucher = v })))
            {
                var id = Convert.ToInt32(d.Detail.Remark);
                if (!bin.Add(id))
                    continue;

                var record = m_Crawler.Result.SingleOrDefault(r => r.Index == id);
                if (record == null)
                    continue;
                // ReSharper disable once PossibleInvalidOperationException
                if (!(Math.Abs(d.Detail.Fund.Value) - record.Fund).IsZero())
                    continue;

                var tmp = d.Voucher.Details.Where(dd => dd.Title == 6602).ToArray();
                var content = tmp.Length == 1 ? tmp.First().Content : string.Empty;
                sb.AppendLine($"{d.Voucher.ID,28}{d.Detail.Remark.CPadRight(20)}{content.CPadRight(20)}{record.Endpoint}");
            }

            return new UnEditableText(sb.ToString());
        }

        /// <summary>
        ///     解析自动补全指令
        /// </summary>
        /// <param name="pars">自动补全指令</param>
        /// <returns>解析结果</returns>
        private Dictionary<DateTime, List<Tuple<RegularType, string>>> GetDic(IReadOnlyList<string> pars)
        {
            var dic = new Dictionary<DateTime, List<Tuple<RegularType, string>>>();
            foreach (var par in pars)
            {
                var xx = par;
                var dt = DateTime.Now.Date;
                try
                {
                    var dd = FacadeF.ParsingF.UniqueTime(ref xx);
                    if (dd == null)
                        throw new ApplicationException("无法处理无穷长时间以前的自动补全指令");

                    dt = dd.Value;
                }
                catch (ApplicationException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // ignored
                }

                var sp = xx.Split(new[] { ' ', '/', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length == 0)
                    continue;

                var lst = new List<Tuple<RegularType, string>>();
                foreach (var s in sp)
                    switch (s.Trim().ToLowerInvariant())
                    {
                        case "sp":
                            lst.Add(new Tuple<RegularType, string>(RegularType.Shopping, "食品"));
                            break;
                        case "sh":
                            lst.Add(new Tuple<RegularType, string>(RegularType.Shopping, "生活用品"));
                            break;
                        case "bg":
                            lst.Add(new Tuple<RegularType, string>(RegularType.Shopping, "办公用品"));
                            break;
                        case "xz":
                            lst.Add(new Tuple<RegularType, string>(RegularType.Charging, "洗澡卡"));
                            break;
                        case "xy":
                            lst.Add(new Tuple<RegularType, string>(RegularType.Charging, "洗衣"));
                            break;
                        default:
                            throw new ArgumentException("未知参数", nameof(pars));
                    }

                dic.Add(dt, lst);
            }

            return dic;
        }
    }
}