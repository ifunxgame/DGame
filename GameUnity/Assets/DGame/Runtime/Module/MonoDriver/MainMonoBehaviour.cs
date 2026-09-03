using System;
using System.Diagnostics;
using UnityEngine;

namespace DGame
{
    internal partial class MonoDriver
    {
        private class MainMonoBehaviour : MonoBehaviour
        {
            private event Action OnUpdateEvent;
            private event Action OnFixedUpdateEvent;
            private event Action OnLateUpdateEvent;
            private event Action OnDestroyEvent;
            private event Action OnDrawGizmosEvent;
            private event Action OnDrawGizmosSelectedEvent;
            private event Action<bool> OnApplicationPauseEvent;

            private void Update()
            {
                OnUpdateEvent?.Invoke();
            }

            private void FixedUpdate()
            {
                OnFixedUpdateEvent?.Invoke();
            }

            private void LateUpdate()
            {
                OnLateUpdateEvent?.Invoke();
            }

            private void OnDestroy()
            {
                // 取出后置空再触发，保证销毁回调在任何销毁顺序下都只执行一次。
                Action destroyEvent = OnDestroyEvent;
                OnDestroyEvent = null;
                destroyEvent?.Invoke();
            }

            [Conditional("UNITY_EDITOR")]
            private void OnDrawGizmos()
            {
                OnDrawGizmosEvent?.Invoke();
            }

            [Conditional("UNITY_EDITOR")]
            private void OnDrawGizmosSelected()
            {
                OnDrawGizmosSelectedEvent?.Invoke();
            }

            private void OnApplicationPause(bool pauseStatus)
            {
                OnApplicationPauseEvent?.Invoke(pauseStatus);
            }

            #region 事件添加删除操作

            public void AddUpdateListener(Action action)
            {
                OnUpdateEvent += action;
            }

            public void RemoveUpdateListener(Action action)
            {
                OnUpdateEvent -= action;
            }

            public void AddFixedUpdateListener(Action action)
            {
                OnFixedUpdateEvent += action;
            }

            public void RemoveFixedUpdateListener(Action action)
            {
                OnFixedUpdateEvent -= action;
            }

            public void AddLateUpdateListener(Action action)
            {
                OnLateUpdateEvent += action;
            }

            public void RemoveLateUpdateListener(Action action)
            {
                OnLateUpdateEvent -= action;
            }

            public void AddDestroyListener(Action action)
            {
                OnDestroyEvent += action;
            }

            public void RemoveDestroyListener(Action action)
            {
                OnDestroyEvent -= action;
            }

            [Conditional("UNITY_EDITOR")]
            public void AddOnDrawGizmosListener(Action action)
            {
                OnDrawGizmosEvent += action;
            }

            [Conditional("UNITY_EDITOR")]
            public void RemoveOnDrawGizmosListener(Action action)
            {
                OnDrawGizmosEvent -= action;
            }

            [Conditional("UNITY_EDITOR")]
            public void AddOnDrawGizmosSelectedListener(Action action)
            {
                OnDrawGizmosSelectedEvent += action;
            }

            [Conditional("UNITY_EDITOR")]
            public void RemoveOnDrawGizmosSelectedListener(Action action)
            {
                OnDrawGizmosSelectedEvent -= action;
            }

            public void AddOnApplicationPauseListener(Action<bool> action)
            {
                OnApplicationPauseEvent += action;
            }

            public void RemoveOnApplicationPauseListener(Action<bool> action)
            {
                OnApplicationPauseEvent -= action;
            }

            #endregion

            public void Destroy()
            {
                // 先触发销毁回调再清空：ModuleSystem.Destroy() 可能先于 GameObject 销毁执行，
                // 若直接清空会导致挂在此处的热更层清理入口被静默跳过。
                Action destroyEvent = OnDestroyEvent;
                OnDestroyEvent = null;
                destroyEvent?.Invoke();

                OnUpdateEvent = null;
                OnFixedUpdateEvent = null;
                OnLateUpdateEvent = null;
                OnDrawGizmosEvent = null;
                OnDrawGizmosSelectedEvent = null;
                OnApplicationPauseEvent = null;
            }
        }
    }
}