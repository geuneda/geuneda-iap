using System;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// IL2CPP 코드 제거(managed stripping)에서 JSON 으로만 만드는 타입을 지키는 표시입니다. Unity 링커는 이름이 PreserveAttribute 인 특성을 모두 같은 뜻으로 봅니다.
	/// 서버와 함께 컴파일하므로 UnityEngine 의 것을 쓰지 않습니다.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field)]
	internal sealed class PreserveAttribute : Attribute
	{
	}
}
