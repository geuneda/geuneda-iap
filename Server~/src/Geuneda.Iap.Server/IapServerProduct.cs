using System;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 서버가 지급하는 상품 하나입니다. 게임 상품 ID 와 스토어별 상품 ID 를 잇습니다. 클라이언트의 <c>IapProductDefinition</c> 과 같은 ID 를 씁니다.
	/// </summary>
	/// <remarks>판매를 그만둔 상품도 빼지 마세요. 이전 실행의 결제가 늦게 올 수 있습니다.</remarks>
	public sealed class IapServerProduct
	{
		/// <param name="id">게임 상품 ID. 비었으면 <see cref="ArgumentException"/></param>
		/// <param name="type">상품 종류. Google 은 이것으로 일회성 상품과 구독 조회를 고릅니다</param>
		/// <param name="googlePlayId">Google Play 상품 ID. null 이면 <paramref name="id"/></param>
		/// <param name="appStoreId">App Store 상품 ID. null 이면 <paramref name="id"/></param>
		public IapServerProduct(string id, IapProductType type, string googlePlayId = null, string appStoreId = null)
		{
			if (string.IsNullOrWhiteSpace(id))
			{
				throw new ArgumentException("Product id is empty", nameof(id));
			}

			if (!Enum.IsDefined(type))
			{
				throw new ArgumentOutOfRangeException(nameof(type), type, "Undefined product type");
			}

			if (googlePlayId != null && string.IsNullOrWhiteSpace(googlePlayId))
			{
				throw new ArgumentException("Google Play id is blank", nameof(googlePlayId));
			}

			if (appStoreId != null && string.IsNullOrWhiteSpace(appStoreId))
			{
				throw new ArgumentException("App Store id is blank", nameof(appStoreId));
			}

			Id = id;
			Type = type;
			GooglePlayId = googlePlayId ?? id;
			AppStoreId = appStoreId ?? id;
		}

		/// <summary>게임 상품 ID</summary>
		public string Id { get; }

		public IapProductType Type { get; }

		public string GooglePlayId { get; }

		/// <summary>App Store·Mac App Store 상품 ID</summary>
		public string AppStoreId { get; }

		public override string ToString()
		{
			return $"{Id} ({Type})";
		}
	}
}
