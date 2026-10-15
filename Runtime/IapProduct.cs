using System;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>스토어에서 조회한 상품 정보(현지화 가격, 이름)입니다. 스토어 어댑터가 만듭니다.</summary>
	public sealed class IapProduct
	{
		/// <param name="definition">이 상품의 정의</param>
		/// <param name="localizedPriceString">스토어가 준 현지화 가격 문자열(예: "₩1,100"). 없으면 빈 문자열</param>
		/// <param name="price">현지 통화 가격. 0 이상</param>
		/// <param name="currencyCode">ISO 4217 통화 코드. 모르면 null</param>
		/// <param name="title">현지화 상품 이름. 없으면 빈 문자열</param>
		/// <param name="description">현지화 상품 설명. 없으면 빈 문자열</param>
		/// <param name="isAvailable">지금 살 수 있는지</param>
		public IapProduct(IapProductDefinition definition, string localizedPriceString, decimal price, string currencyCode, string title,
			string description, bool isAvailable)
		{
			if (price < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(price), price, "Price must not be negative");
			}

			Definition = definition ?? throw new ArgumentNullException(nameof(definition));
			LocalizedPriceString = localizedPriceString ?? string.Empty;
			Price = price;
			CurrencyCode = string.IsNullOrEmpty(currencyCode) ? null : currencyCode;
			Title = title ?? string.Empty;
			Description = description ?? string.Empty;
			IsAvailable = isAvailable;
		}

		public IapProductDefinition Definition { get; }

		public string Id => Definition.Id;

		public IapProductType Type => Definition.Type;

		public string LocalizedPriceString { get; }

		public decimal Price { get; }

		public string CurrencyCode { get; }

		public string Title { get; }

		public string Description { get; }

		public bool IsAvailable { get; }
	}
}
