using System;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 게임이 파는 상품 하나의 정의입니다. 서비스는 시작할 때 이 목록으로 스토어 상품을 조회합니다.
	/// </summary>
	/// <remarks>
	/// 판매를 그만둔 상품도 목록에서 빼지 마세요. 정의에 없는 상품의 미확정 결제는 스토어가 상품 종류를 몰라
	/// 지급도 확정도 하지 않습니다(Google 은 종류를 모르는 주문을 확정하면 소모 대신 승인만 해서 영구 보유 상태가 됨).
	/// </remarks>
	public sealed class IapProductDefinition
	{
		/// <param name="id">게임이 쓰는 상품 ID. 비었으면 <see cref="ArgumentException"/></param>
		/// <param name="type">상품 종류</param>
		/// <param name="storeSpecificId">지금 플랫폼 스토어에 등록한 상품 ID. null 이면 <paramref name="id"/> 와 같습니다</param>
		public IapProductDefinition(string id, IapProductType type, string storeSpecificId = null)
		{
			if (string.IsNullOrWhiteSpace(id))
			{
				throw new ArgumentException("Product id is empty", nameof(id));
			}

			if (!Enum.IsDefined(typeof(IapProductType), type))
			{
				throw new ArgumentOutOfRangeException(nameof(type), type, "Undefined product type");
			}

			if (storeSpecificId != null && string.IsNullOrWhiteSpace(storeSpecificId))
			{
				throw new ArgumentException("Store specific id is blank", nameof(storeSpecificId));
			}

			Id = id;
			Type = type;
			StoreSpecificId = storeSpecificId ?? id;
		}

		/// <summary>게임이 쓰는 상품 ID</summary>
		public string Id { get; }

		public IapProductType Type { get; }

		/// <summary>지금 플랫폼 스토어에 등록한 상품 ID</summary>
		public string StoreSpecificId { get; }

		public override string ToString()
		{
			return Id == StoreSpecificId ? $"{Id} ({Type})" : $"{Id} ({Type}, store id {StoreSpecificId})";
		}
	}
}
