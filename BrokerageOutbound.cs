using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Zebra.FileBank;
using Zebra.FileBank.Client;
using Zebra.IC.WebUI.Ext;
using Zebra.InfoPath;
using Zebra.InfoPath.Repos;
using Zebra.InfoPath.SDK;
using Zebra.InfoPath.SDK.Buytong;
using Zebra.InfoPath.SDK.HongYuan;
using Zebra.InfoPath.SDK.Linehual;
using Zebra.InfoPath.SDK.NewRealm;
using Zebra.InfoPath.SDK.SkyPostal;
using Zebra.InfoPath.SDK.Usps;
using Zebra.InfoPath.SDK.WTD;
using Zebra.InfoPath.SDK.WTD.Services;
using Zebra.InfoPath.SDK.YiyunCang;

namespace Zebra.IC.WebUI.Services
{
	[Queue("uncritical")]
	public interface IBrokerageOutbound : IDequeue
	{
		string NoticeHy(Parcel.ClearanceForHy parcel);
	}
	[Queue("uncritical")]
	public interface IOutboundNotice
	{
		Task<string> Notice(Parcel.List parcel);
	}
	[Queue("uncritical")]
	public partial class BrokerageOutbound : IBrokerageOutbound, IOutboundNotice
	{
		private readonly IParcelRepo _parcelRepo;
		private readonly ICacheRepo _cacheRepo;
		private readonly IBrkgRepo _brkgRepo;
		private readonly IOptions<AppConfig> _config;
		private readonly IOptions<HongYuanConfig> _hongYuanConfig;
		private readonly IOptions<PostalCodeConfig> _postalCodeConfig;

		//private readonly IFileService _fileService;
		private readonly IFileBankClient _fileBankClient;
		private readonly ILinehualOrderService _linehualOrderService;

		public BrokerageOutbound(IParcelRepo parcelRepo,
			ICacheRepo cacheRepo,
			IBrkgRepo brkgRepo,
			IOptions<AppConfig> config,
			IOptions<HongYuanConfig> hongYuanConfig,
			IOptions<PostalCodeConfig> postalCodeConfig,
			//IFileService fileService,
			IFileBankClient fileBankClient,
		ILinehualOrderService linehualOrderService)
		{
			_brkgRepo = brkgRepo;
			_cacheRepo = cacheRepo;
			_parcelRepo = parcelRepo;

			_config = config;
			_hongYuanConfig = hongYuanConfig;
			_postalCodeConfig = postalCodeConfig;

			//_fileService = fileService;
			_fileBankClient = fileBankClient;
			_linehualOrderService = linehualOrderService;
		}

		public string Dequeue()
		{
			// TODO: Move fixed @source & @Qtype to Enum
			var list = _brkgRepo.BrokerageDequeue(Source.IDs.InfoPath.ToInt32(),
				QueueType.BrokerApi.ToInt32()).ToList();
			if (!list.Any()) return JsonConvert.SerializeObject(new { Message = "Empty List" });
			list.nlog();
			var parcels = _parcelRepo.ListVia(list.Select(x => x.MatterID))
				.EachDo(x => x.PostalCodeForCN = mapPostalCodeForCN(x.To));

			var parcelIDs = parcels.Select(x => x.ID).ToList();
			var notInList = list.Where(x => !parcelIDs.Contains(x.MatterID)).Select(y => y.MatterID).ToList();

			var parcelGroups = parcels.GroupBy(x => x.BrokerID).ToListDuly();
			foreach (var group in parcelGroups)
			{
				switch ((Broker)group.Key)
				{
					case Broker.HongYuan:
						noticeHongyuan(group.Select(x => x));
						break;
				}
			}
			foreach (var parcel in parcelGroups.Where(x => x.Key != Broker.HongYuan.ToInt32()).SelectMany(x => x))
			{
				BackgroundJob.Enqueue<IOutboundNotice>(x => x.Notice(parcel));
			}

			var tvp = notInList.Select(x => at.Tvp.Duad.Join(x, "超出导入数据量")).Over(at.Tvp.Many.Join);
			_brkgRepo.RcvBrkgRejection(tvp);

			return JsonConvert.SerializeObject(new { Rejected = notInList, Processing = parcelIDs });
		}

		public void noticeHongyuan(IEnumerable<Parcel.List> lists)
		{
			var parcels = _parcelRepo.ClearancesForHy(new Parcel.ClearanceForHy.Criteria { IDs = lists.Select(x => x.ID).ToListDuly() });
			foreach (var parcel in parcels)
			{
				BackgroundJob.Enqueue<IBrokerageOutbound>(x => x.NoticeHy(parcel));
			}
		}

		public async Task<string> Notice(Parcel.List parcel)
		{
			try
			{
				// TODO: Move int value to Enum @ BrokerID
				switch ((Broker)parcel.BrokerID)
				{
					case Broker.NewRealm:
						return await noticeNewRealm(parcel.ID);
					case Broker.USPS:
						return await noticeUsps(parcel);
					case Broker.WTD:
						return await noticeWtd(parcel.ID);
					case Broker.WTD_CC:
						return await noticeWtdCC(parcel);
					case Broker.Buytong:
						return await noticeBuytongAsync(parcel.ID);
					case Broker.YiyunCang:
						return await noticeYiyunCang(parcel);
					case Broker.LineHaul:
						return noticeLinehualNew(parcel);
					case Broker.COE:
						return noticeCOE(parcel);
					case Broker.MEX_SkyPostal:
						return await noticeSkyPostal(parcel);
					default:
						switch ((Route.IDs)parcel.RouteID)
						{
							//case Route.IDs.HK_eExpress_NCN:
							//case Route.IDs.HK_eExpress:
							case Route.IDs.VMI_HKPost_ARM_SaSa:
							case Route.IDs.VMI_HKPost_eTK_SaSa:
								return noticeHKPostAsync(parcel);
							default:
								return unSupportedBroker(parcel);
						}
				}
			}
			catch (Exception e)
			{
				new { Response = "Brokerage Outbound Dequeue Exception", MatterId = parcel.ID, Result = e }.LogInfo();
				_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(parcel.ID, e.Message));
				return string.Empty;
			}
		}

		public string NoticeHy(Parcel.ClearanceForHy parcel)
		{
			switch (parcel.Route)
			{
				case Route.IDs.BC_T_XHY:
					return noticeHongYuanCZ(parcel);
				default:
					return unSupportedRoute(parcel);
			}
		}

		private string acceptanceTvp(long parcelID, string courierAlias, string trackingNbr, string dozenInfo)
			=> at.Tvp.Quad.Join(parcelID, courierAlias, trackingNbr, dozenInfo);
		private string rejectionTvp(long parcelID, string msg) => at.Tvp.Duad.Join(parcelID, msg);

		private string unSupportedBroker(Parcel.List parcel)
		{
			_brkgRepo.RcvBrkgRejection(rejectionTvp(parcel.ID, "Unsupported Broker Provider"));
			return JsonConvert.SerializeObject(new
			{
				parcel,
				result = "Unsupported Broker Provider"
			});
		}
		private string unSupportedRoute(Parcel.ClearanceForHy parcel)
		{
			_brkgRepo.RcvBrkgRejection(rejectionTvp(parcel.ID, "Unsupported Broker-Route Provider"));
			return JsonConvert.SerializeObject(new
			{
				parcel,
				result = "Unsupported Broker-Route Provider"
			});
		}

		private string noticeHongYuanCZ(Parcel.ClearanceForHy parcel)
		{
			var svc = new InfoPath.SDK.HongYuan.OrderService(_hongYuanConfig);
			var result = svc.Send(parcel.ToHYOrder());

			if (result.IsSuccess)
			{
				BackgroundJob.Schedule(() => SendHYTrackingInfo(parcel, result), TimeSpan.FromMinutes(1));
			}
			else
			{
				_brkgRepo.RcvBrkgRejectionPostOutgated(rejectionTvp(parcel.ID, result.Msg));
			}
			return JsonConvert.SerializeObject(new
			{
				parcel,
				result
			});
		}

		public string SendHYTrackingInfo(Parcel.ClearanceForHy parcel, InfoPath.SDK.HongYuan.Models.Order.Response ordResult)
		{
			var svc = new DeliveryOrderService(_hongYuanConfig);
			var result = svc.Send(new InfoPath.SDK.HongYuan.Models.DeliveryOrder
			{
				MIC = parcel.Mic,
				POD = parcel.POD,
				Weight = Math.Round(parcel.Weight.Kg, 4),
				PostedOn = parcel.PostedOn,
				Currency = parcel.BrokerageInfos.FirstOrDefault().LineInfo.LineTotal.Currency.ToString(),
				ShippingInfo = new InfoPath.SDK.HongYuan.Models.ShippingInfo
				{
					ShprName = parcel.From.Name,
					ShprAddress = parcel.From.AddressLine1,
					ShprCity = parcel.From.City,
					ShprCountryCode = parcel.From.CountryCode,
					ShprPhone = parcel.From.Phone,
					CneeCity = parcel.To.City,
					CneeCountryCode = parcel.To.CountryCode,
					CneeName = parcel.To.Name,
					CneeStreet1 = parcel.To.Street1,
					CneeFullAddress = parcel.To.AddressLineCN,
					CneeDistrict = parcel.To.District,
					CneePhone = parcel.To.Phone,
					CneeProvince = parcel.To.Province
				},
				TrackingNbr = parcel.TrackingNbr,
				FlightNbr = parcel.FlightNbr,
				MawbNbr = parcel.MawbNbr,
			});
			if (result.IsSuccess)
			{
				_brkgRepo.RcvBrkgAcceptancePostOutgated(parcel.ID.ToString());
			}
			else
			{
				_brkgRepo.RcvBrkgRejectionPostOutgated(rejectionTvp(parcel.ID, result.Msg));
			}
			return JsonConvert.SerializeObject(new
			{
				parcel,
				result
			});
		}

		private async Task<string> noticeNewRealm(long id)
		{
			new { Notice = "NewRealm", MatterId = id }.nlog();
			var p = _parcelRepo.ForBrokerage(new Parcel.ForBrokerage.Criteria { ID = id }).FirstOrDefault();
			new { Request = "NewRealm", MatterId = id, Data = p }.nlog();
			var result = await new Zebra.InfoPath.SDK.NewRealm.NoticeService(_config.Value.IsProduction).Notice(p);
			new { Response = "NewRealm", MatterId = id, Result = result }.nlog();

			if (result.IsSuccessed() && result.MainNo.Trim().IsTangible())
			{
				_brkgRepo.RcvBrkgAcceptance(result.AcceptanceTvp(p.ID));
				return JsonConvert.SerializeObject(new
				{
					parcel = p.ID,
					request = p.ForNewRealm(),
					result = result
				});
			}

			_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(p.ID, result.Message));
			return JsonConvert.SerializeObject(new
			{
				parcel = p.ID,
				request = p.ForNewRealm(),
				result = result
			});
		}

		private async Task<string> noticeUsps(Parcel.List parcel)
		{
			new { Notice = "Usps", MatterId = parcel.ID, Data = parcel }.nlog();
			var result = await new Zebra.InfoPath.SDK.Usps.NoticeService(_config.Value.IsProduction).Notice(parcel);
			new { Response = "Usps", MatterId = parcel.ID, Result = result }.nlog();
			if (result.IsSuccessed() && result.TrackingNbr.IsTangible())
			{
				try
				{
					/*
					var files = result.Labels
					.EachTo(async x => await _fileService.Upload(ZebraFile.Of(x, result.TrackingNbr)))
					.EachTo(x => x.Result);
					new { Response = "USPS files", MatterId = parcel.ID, Result = files }.LogInfo();
					_brkgRepo.RcvBrkgAcceptance(result.AcceptanceTvp(files.EachTo(x => x.FileID)));
					*/
					result.Labels.EachTo(x => _fileBankClient.InsertFile(parcel.RcvHub.ToTerritory(), result.TrackingNbr, x))
						.OnAllSuccess(x => _brkgRepo.RcvBrkgAcceptance(result.AcceptanceTvp(x)))
						.OnAllSuccess(x => new { Response = "USPS files", MatterId = parcel.ID, Result = x }.LogInfo())
						.OnAnyFailure(x => throw new Exception(x.Value));
				}
				catch (Exception ex)
				{
					_brkgRepo.RcvBrkgAcceptance(result.AcceptanceTvp());
					new { Response = "USPS Filebank Exception", MatterId = parcel.ID, Exception = ex }.LogInfo();
				}

				return JsonConvert.SerializeObject(new
				{
					parcel = parcel.ID,
					request = parcel.ForUsps()
				});
			}

			_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(parcel.ID, result.Error));
			return JsonConvert.SerializeObject(new
			{
				parcel = parcel.ID,
				request = parcel.ForUsps()
			});
		}

		/// <summary>
		/// 本期暂时只支持单个包裹推送
		/// </summary>
		/// <param name="parcel">parcel</param>
		/// <returns></returns>
		private async Task<string> noticeWtdCC(Parcel.List parcel)
		{
			var id = parcel.ID;
			new { Notice = "WTD-CC", MatterId = id }.nlog();

			var parcels = new List<Parcel.List> { parcel };
			var trackingNbrs = _cacheRepo.FetchCourierNbrs(parcel.CourierID, parcels.Count);
			parcels.EachDo((x, i) =>
			{
				x.FilledLastMilerNbr = trackingNbrs[i];
			});

			new { Request = "WTD-CC", MatterId = parcel.ID, Data = parcels }.nlog();
			var result = await new InfoPath.SDK.WTD.NoticeService().CCNotice(parcels);
			new { Response = "WTD-CC", MatterId = parcel.ID, Result = result }.nlog();
			if (result.IsSuccess && result.response.updateCount > 0)
			{
				// get tracking and accept
				_brkgRepo.RcvBrkgAcceptance(result.response.AcceptanceTvp(id));
			}
			else
			{
				_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(id, result.response.errorInfoList.errorInfos.FirstOrDefault().errMsg));
			}

			return JsonConvert.SerializeObject(new
			{
				parcel = parcel,
				request = parcel.ForWTD(),
				result = result
			});
		}

		/// <summary>
		/// 本期暂时只支持单个包裹推送
		/// </summary>
		/// <param name="id">parcel ID</param>
		/// <returns></returns>
		private async Task<string> noticeWtd(long id)
		{
			new { Notice = "WTD", MatterId = id }.nlog();
			var parcel = _parcelRepo.ForBrokerage(new Parcel.ForBrokerage.Criteria { ID = id }).FirstOrDefault();

			if (parcel == null)
			{
				var msg = $"Parcel: {id} not exists";
				_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(id, msg));
				return JsonConvert.SerializeObject(new
				{
					parcel = id,
					result = msg
				});
			}

			new { Request = "WTD", MatterId = id, Data = parcel }.nlog();
			var result = await new InfoPath.SDK.WTD.NoticeService().Notice(parcel);
			new { Response = "WTD", MatterId = id, Result = result }.nlog();
			if (result.IsSuccess && result.response.updateCount > 0)
			{
				BackgroundJob.Schedule(() => GetWtdWaybill(parcel.ID, parcel.RefNbr.MIC()), TimeSpan.FromMinutes(5));
			}
			else
			{
				_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(id, result.response.errorInfoList.errorInfos.FirstOrDefault().errMsg));
			}

			return JsonConvert.SerializeObject(new
			{
				parcel = parcel,
				request = parcel.ForWTD(),
				result = result
			});
		}

		public async Task<string> GetWtdWaybill(long parcelId, string mic)
		{
			var result = await WaybillService.GetWaybill(new List<Referring> { Referring.Of(parcelId, mic) });
			new { Response = "WTD", MatterId = parcelId, Result = result }.nlog();
			if (!result.IsSuccess ||
				!(result.response?.CourierTrackings?.FirstOrDefault()?.TrackingNbr ?? string.Empty).IsTangible())
			{
				_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(parcelId, result.response.desc));
				return JsonConvert.SerializeObject(new
				{
					parcel = parcelId,
					result = $"Parcel Mic: {mic} get waybill error"
				});
			}
			_brkgRepo.RcvBrkgAcceptance(result.response.AcceptanceTvp(parcelId));

			return JsonConvert.SerializeObject(new
			{
				parcel = parcelId,
				result = result
			});
		}

		private async Task<string> noticeBuytongAsync(long id)
		{
			new { Notice = "Buytong", MatterId = id }.nlog();
			var parcel = _parcelRepo.ForBrokerage(new Parcel.ForBrokerage.Criteria { ID = id }).FirstOrDefault();
			new { Request = "Buytong", MatterId = id, Data = parcel }.nlog();
			var result = await new InfoPath.SDK.Buytong.NoticeService().Notice(parcel);
			new { NoticeResponse = "Buytong", MatterId = id, Result = result }.nlog();
			if (!result.IsSuccess)
			{
				_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(id, result.ErrorMsg ?? result.Data));
			}
			else
			{
				BackgroundJob.Schedule(() => GetBuytongTrackingNbrAsync(parcel), TimeSpan.FromMinutes(5));
			}

			return JsonConvert.SerializeObject(new
			{
				parcel = parcel,
				request = parcel.ForBuytong(),
				result = result
			});
		}

		public async Task<string> GetBuytongTrackingNbrAsync(Parcel.ForBrokerage parcel)
		{
			new { FetchNbr = "Buytong", MatterId = parcel.ID, MIC = parcel.RefNbr.MIC() }.nlog();
			var trackingResult = await InfoPath.SDK.Buytong.Services.OrderService.GetTrackingNbr(parcel.RefNbr.MIC());
			new { OrderServiceResponse = "Buytong", MatterId = parcel.ID, TrackingResult = trackingResult }.nlog();
			var pdfResult = await InfoPath.SDK.Buytong.Services.PdfService.PdfResults(trackingResult.Data.BeEnumerable());
			new { PdfServiceResponse = "Buytong", MatterId = parcel.ID, PdfResult = pdfResult }.nlog();
			//var fileRes = _fileService.DownloadUrl(pdfResult.Select(x => x.Data.PDF).FirstOrDefault()).Result;
			//_brkgRepo.RcvBrkgAcceptance(trackingResult.AcceptanceTvp(parcel.ID, fileRes.FileID));
			var newFileRes = _fileBankClient.InsertFile(parcel.RcvHub.ToTerritory(), parcel.RefNbr.MIC(), new Uri(pdfResult.Select(x => x.Data.PDF).FirstOrDefault())).ReduceOrThrow();
			_brkgRepo.RcvBrkgAcceptance(trackingResult.AcceptanceTvp(parcel.ID, newFileRes));

			return JsonConvert.SerializeObject(new
			{
				Parcel = parcel,
				Request = parcel.ForBuytong(),
				TrackingResult = trackingResult,
				PdfResult = pdfResult
			});
		}

		/// <summary>
		/// 本期暂时只支持单个包裹推送
		/// </summary>
		/// <param name="parcel">parcel</param>
		/// <returns></returns>
		private async Task<string> noticeYiyunCang(Parcel.List parcel)
		{
			var id = parcel.ID;
			new { Notice = "YiyunCang", MatterId = id }.LogInfo();

			new { Request = "YiyunCang", MatterId = parcel.ID, Data = parcel }.nlog();
			var result = await new InfoPath.SDK.YiyunCang.NoticeService(_config.Value.IsProduction).Notice(new List<Parcel.List> { parcel });
			new { Response = "YiyunCang", MatterId = parcel.ID, Result = result }.LogInfo();
			if (result.IsSuccess)
			{
				var acceptanceTvp = at.Tvp.Quad.Join(id, "EMS (YYC)", result.TrackingParas.FirstOrDefault()?.Text);
				_brkgRepo.RcvBrkgAcceptance(acceptanceTvp);
			}
			else
			{
				var msg = result.ErrorDesc + ":" + string.Join(";", result.ErrorDetail.Select(x => x.ErrorDes));
				_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(id, msg));
			}

			return JsonConvert.SerializeObject(new
			{
				parcel = parcel,
				request = parcel.ForYiyunCang(),
				result = result
			});
		}

		// HK2CN 'LineHaul -e特快'
		// TODO: Drop this when new linehual is stable.
		private string XXXnoticeLineHaul(Parcel.List parcel)
		{
			new { Notice = "LineHaul -e特快", MatterId = parcel.ID, Data = parcel }.nlog();

			if (parcel.IsTruthy() && ((SvcType)(parcel.SvcType / 10000)).IsGTSSeekPostalCode())
			{
				parcel.Do(x =>
				{
					x.PostalCodeForGTS = JsonReader<PostalCodeForGTS>
						.GetGTSPostalCodes(_postalCodeConfig.Value.PathForGTS)
						.Where(p => (p.Province.EqualSafely(x.To.Province)
							|| p.FuzzyProvince.EqualSafely(x.To.Province))
						&& p.City.EqualSafely(x.To.City)
						&& p.District.EqualSafely(x.To.District))
						.FirstOrDefault()?.PostalCode ?? string.Empty;
				});
			}
			var result = new InfoPath.SDK.ETK.NoticeService().Notice(parcel);

			new { Response = "LineHaul -e特快", MatterId = parcel.ID, Result = result }.nlog();
			if (!result.Result || !result.ShipmentNumber.IsTangible())
			{
				_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(parcel.ID, result.Message));
				return result.Message;
			}
			/*
			var fileRes = await _fileService.Upload(ZebraFile.Of(result.ShipmentLabel, result.ShipmentNumber));
			result.FileBankID = fileRes.FileID;
			new { Response = "LineHaul -e特快 fileRes", MatterId = parcel.ID, Result = fileRes }.LogInfo();
			*/

			_fileBankClient.InsertFile(parcel.RcvHub.ToTerritory(), result.ShipmentNumber, result.ShipmentLabel)
				.OnSuccess(x => result.FileBankID = x)
				.OnSuccess(x => new { Response = "LineHaul -e特快 fileRes", MatterId = parcel.ID, Result = x }.LogInfo())
				.OnFailure(x => throw new Exception(x.Value));

			var acceptanceTvp = result.AcceptanceTvp(parcel.ID);

			new { Response = "LineHaul -e特快 acceptanceTvp", MatterId = parcel.ID, Result = acceptanceTvp }.LogInfo();
			_brkgRepo.RcvBrkgAcceptance(acceptanceTvp);

			return JsonConvert.SerializeObject(new
			{
				parcel = parcel.ID,
				request = parcel.ForUsps(),
				result = result
			});
		}

		/// <summary>
		/// GTS - Linehual New
		/// </summary>
		private string noticeLinehualNew(Parcel.List parcel)
		{
			if (parcel.IsTruthy() && ((SvcType)(parcel.SvcType / 10000)).IsGTSSeekPostalCode())
			{
				parcel.Do(x =>
				{
					x.PostalCodeForGTS = JsonReader<PostalCodeForGTS>
						.GetGTSPostalCodes(_postalCodeConfig.Value.PathForGTS)
						.Where(p => (p.Province.EqualSafely(x.To.Province)
							|| p.FuzzyProvince.EqualSafely(x.To.Province))
						&& p.City.EqualSafely(x.To.City)
						&& p.District.EqualSafely(x.To.District))
						.FirstOrDefault()?.PostalCode ?? x.To.PostalCode;
				});
			}
			var result = _linehualOrderService.Send(parcel.ToLinehual());

			new { Response = "LineHaul -New", MatterId = parcel.ID, Result = result }.nlog();
			if (!result.IsSuccess || !result.TrackingNbr.IsTangible())
			{
				_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(parcel.ID, result.Msg));
				return result.Msg;
			}

			BackgroundJob.Schedule(() => UploadFileBank(result.Data, result.TrackingNbr, parcel, Courier.eExpress), TimeSpan.FromSeconds(10));
			return JsonConvert.SerializeObject(new
			{
				parcel = parcel.ID,
				request = parcel,
				result = result
			});
		}
		/*
		public async Task<string> UploadFileBank(string label, string trackingNbr, long parcelID, Courier courier)
		{
			var fileRes = await _fileService.Upload(ZebraFile.Of(Convert.FromBase64String(label), trackingNbr));
			var tvp = acceptanceTvp(parcelID, courier.ToDescription(), trackingNbr, fileRes.FileID);
			new { Response = "UploadFileBank - acceptanceTvp", MatterId = parcelID, Result = tvp }.LogInfo();
			_brkgRepo.RcvBrkgAcceptance(tvp);
			return tvp;
		}
		*/

		public void UploadFileBank(string label, string trackingNbr, Parcel.List parcel, Courier courier)
		{
			_fileBankClient.InsertFile(parcel.RcvHub.ToTerritory(), trackingNbr, Convert.FromBase64String(label))
				.OnSuccess(x =>
				{
					var tvp = acceptanceTvp(parcel.ID, courier.ToDescription(), trackingNbr, x);
					new { Response = "UploadFileBank - acceptanceTvp", MatterId = parcel.ID, Result = tvp }.LogInfo();
					_brkgRepo.RcvBrkgAcceptance(tvp);
				})
				.OnFailure(x => throw new Exception(x.Value));
		}

		/// <summary>
		/// HK2CN 'COE -e特快'
		/// </summary>
		private string noticeCOE(Parcel.List parcel)
		{
			new { Notice = "COE -e特快", MatterId = parcel.ID, Data = parcel }.nlog();
			InfoPath.SDK.COE.ImportResult result = null;

			result = new InfoPath.SDK.COE.NoticeService().Notice(parcel);
			new { Response = "COE -e特快", MatterId = parcel.ID, Result = result }.nlog();
			if (!result.Success || !result.TrackingNbr.IsTangible())
			{
				_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(parcel.ID, result.Message));
				return result.Message;
			}

			//var fileRes = await _fileService.DownloadUrl(result.PdfUrl);
			var newFileRes = _fileBankClient.InsertFile(parcel.RcvHub.ToTerritory(), parcel.RefNbr.MIC(), new Uri(result.PdfUrl)).ReduceOrThrow();
			result.SetFileBankID(newFileRes);
			new { Response = "COE -e特快 fileRes", MatterId = parcel.ID, Result = newFileRes }.LogInfo();
			var acceptanceTvp = result.AcceptanceTvp(parcel.ID);
			new { Response = "COE -e特快 acceptanceTvp", MatterId = parcel.ID, Result = acceptanceTvp }.LogInfo();
			_brkgRepo.RcvBrkgAcceptance(acceptanceTvp);

			return JsonConvert.SerializeObject(new
			{
				parcel = parcel.ID,
				request = parcel.ForUsps(),
				result = result
			});
		}

		/// <summary>
		/// HK Post 
		/// HongKong Post Air Registered Mail ---香港到全球
		/// Hongkong Post eExpress ---香港到除大陆以外地区
		/// </summary>
		private string noticeHKPostAsync(Parcel.List parcel)
		{
			new { Notice = $"HK Post -{parcel.RouteCode}", MatterId = parcel.ID, Data = parcel }.nlog();
			var result = new InfoPath.SDK.HKETK.NoticeService().Notice(parcel);
			new { Response = $"HK Post -{parcel.RouteCode}", MatterId = parcel.ID, Result = result }.nlog();
			if (!result.Success)
			{
				_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(parcel.ID, result.errMessage));
				return result.errMessage;
			}

			var data = InfoPath.SDK.HKETK.OrderService.GetLabelBytes(result.TrackingNbr);
			/*
			new { Response = "HK Post -GetLabelBytes", MatterId = parcel.ID, Result = data }.LogInfo();
			var fileRes = await _fileService.Upload(ZebraFile.Of(data, result.TrackingNbr));
			new { Response = "HK Post -fileRes", MatterId = parcel.ID, Result = fileRes }.LogInfo();
			result.FileBankID = fileRes.FileID;
			*/
			_fileBankClient.InsertFile(parcel.RcvHub.ToTerritory(), result.TrackingNbr, data)
				.OnSuccess(x => result.FileBankID = x)
				.OnSuccess(x => new { Response = "HK Post -fileRes", MatterId = parcel.ID, Result = x }.LogInfo())
				.OnFailure(x => throw new Exception(x.Value));

			var acceptanceTvp = result.AcceptanceTvp(parcel.ID);
			new { Response = "HK Post -acceptanceTvp", MatterId = parcel.ID, Result = acceptanceTvp }.LogInfo();
			_brkgRepo.RcvBrkgAcceptance(acceptanceTvp);

			return JsonConvert.SerializeObject(new
			{
				parcel = parcel.ID,
				request = parcel.ForUsps(),
				result = result
			});
		}

		private string mapPostalCodeForCN(Contact to)
		{
			if (!to.CountryCode.IsCN()) return to.PostalCode;

			var mapCityCodes = JsonReader<PostalCodeForCN>
				.GetCNPostalCodes(_postalCodeConfig.Value.PathForCN)
				.Where(p => (p.Province.EqualSafely(to.Province)
					|| p.FuzzyProvince.EqualSafely(to.Province))
				&& p.City.ContainSoftly(to.City));

			var postalCode = mapCityCodes.Where(x => x.District.ContainSoftly(to.District))
					.FirstOrDefault()?.PostalCode ?? string.Empty;

			if (postalCode.IsEmpty())
				postalCode = mapCityCodes.FirstOrDefault()?.CityPostalCode ?? string.Empty;

			return postalCode ?? to.PostalCode;
		}

		private async Task<string> noticeSkyPostal(Parcel.List parcel)
		{
			var modelForSkyPostal = ParcelToSkyPostalModel(parcel);
			var result = await WebService.PostShipment(modelForSkyPostal);
			if (result.IsSuccess)
			{
				var orgZplBytes = Convert.FromBase64String(result.Data.FirstOrDefault()?.LabelZpl);
				var fileBankID = _fileBankClient.InsertFile(parcel.RcvHub.ToTerritory(), $"{parcel.MIC}.zpl", orgZplBytes).ReduceOrThrow();
				var acceptanceTvp = at.Tvp.Quad.Join(parcel.ID, Courier.MEX_SkyPostal.ToDescription(), result.Data.FirstOrDefault().LabelTrackingNumberForFirst, fileBankID);
				_brkgRepo.RcvBrkgAcceptance(acceptanceTvp);
			}
			else
				_brkgRepo.RcvBrkgRejection(at.Tvp.Duad.Join(parcel.ID, result.ErrorMsg()));

			return JsonConvert.SerializeObject(new
				{
					Parcel = parcel.ID,
					Request = modelForSkyPostal,
					Result = result
				});

		}
	}
}
