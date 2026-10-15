using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	/// <summary>설정의 상품 목록을 게임 상품 ID 로 찾습니다. 목록 검사는 <see cref="Validate"/> 가 시작할 때 합니다.</summary>
	internal sealed class IapProductCatalog
	{
		private readonly Dictionary<string, IapServerProduct> _products = new Dictionary<string, IapServerProduct>(StringComparer.Ordinal);
		private readonly Dictionary<string, IapServerProduct> _googlePlay = new Dictionary<string, IapServerProduct>(StringComparer.Ordinal);
		private readonly Dictionary<string, IapServerProduct> _appStore = new Dictionary<string, IapServerProduct>(StringComparer.Ordinal);

		public IapProductCatalog(IOptions<IapServerOptions> options)
		{
			foreach (var product in options.Value.Products)
			{
				_products.Add(product.Id, product);
				_googlePlay.Add(product.GooglePlayId, product);
				_appStore.Add(product.AppStoreId, product);
			}
		}

		public bool TryGet(string productId, out IapServerProduct product)
		{
			product = null;
			return productId != null && _products.TryGetValue(productId, out product);
		}

		/// <summary>원장 스토어(<see cref="IapStoreNames.GooglePlay"/>, <see cref="IapStoreNames.AppleAppStore"/>)의 스토어 상품 ID 로 찾습니다(서버 알림).</summary>
		public bool TryGetByStoreProductId(string store, string storeProductId, out IapServerProduct product)
		{
			product = null;

			if (storeProductId == null)
			{
				return false;
			}

			switch (store)
			{
				case IapStoreNames.GooglePlay:
					return _googlePlay.TryGetValue(storeProductId, out product);
				case IapStoreNames.AppleAppStore:
					return _appStore.TryGetValue(storeProductId, out product);
				default:
					return false;
			}
		}

		/// <summary>상품이 하나도 없거나, null 이 있거나, 게임 ID·스토어 ID 가 겹치면 이유를, 아니면 null 을 돌려줍니다.</summary>
		public static string Validate(IapServerOptions options)
		{
			if (options.Products.Count == 0)
			{
				return "IapServerOptions.Products is empty";
			}

			var ids = new HashSet<string>(StringComparer.Ordinal);
			var googleIds = new HashSet<string>(StringComparer.Ordinal);
			var appleIds = new HashSet<string>(StringComparer.Ordinal);

			foreach (var product in options.Products)
			{
				if (product == null)
				{
					return "IapServerOptions.Products contains null";
				}

				if (!ids.Add(product.Id))
				{
					return $"Duplicate product id {product.Id}";
				}

				// 서버 알림은 스토어 상품 ID 로 상품을 찾으므로 스토어 안에서도 겹치면 안 된다
				if (!googleIds.Add(product.GooglePlayId))
				{
					return $"Duplicate Google Play product id {product.GooglePlayId}";
				}

				if (!appleIds.Add(product.AppStoreId))
				{
					return $"Duplicate App Store product id {product.AppStoreId}";
				}
			}

			if (options.AcknowledgementRetryInterval <= TimeSpan.Zero)
			{
				return "IapServerOptions.AcknowledgementRetryInterval must be positive";
			}

			return options.MaxRequestBytes < 1 ? "IapServerOptions.MaxRequestBytes must be at least 1" : null;
		}
	}
}
