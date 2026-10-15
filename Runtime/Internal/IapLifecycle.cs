using UnityEngine;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary><see cref="IapService"/> 가 플레이 중에 만드는 숨은 컴포넌트입니다. 앱 일시정지·복귀를 넘깁니다.</summary>
	[AddComponentMenu("")]
	internal sealed class IapLifecycle : MonoBehaviour
	{
		private IapService _service;

		public void Bind(IapService service)
		{
			_service = service;
		}

		public void Unbind()
		{
			_service = null;
		}

		private void OnApplicationPause(bool paused)
		{
			_service?.HandlePause(paused);
		}
	}
}
