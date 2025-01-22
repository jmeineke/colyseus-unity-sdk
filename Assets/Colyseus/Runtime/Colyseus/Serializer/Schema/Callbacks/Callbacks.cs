using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Colyseus.Schema
{
	/// <summary>
	///     Delegate for handling events given a <paramref name="key" /> and a <paramref name="value" />
	/// </summary>
	/// <typeparam name="T"></typeparam>
	/// <param name="currentValue"></param>
	/// <param name="previousValue"></param>
	public delegate void PropertyChangeEventHandler<T>(T currentValue, T previousValue);

	/// <summary>
	///     Delegate function when any property on the schema structure has changed.
	/// </summary>
	public delegate void OnChangeEventHandler();

	/// <summary>
	///     Delegate function for handling <see cref="Schema" /> removal
	/// </summary>
	public delegate void OnRemoveEventHandler();

	/// <summary>
	///     Delegate for handling events given a <paramref name="key" /> and a <paramref name="value" />
	/// </summary>
	/// <param name="value">The affected value</param>
	/// <param name="key">The key we're affecting</param>
	/// <typeparam name="K">The type of <see cref="object" /> we're attempting to access</typeparam>
	/// <typeparam name="T">The <see cref="Schema" /> type</typeparam>
	public delegate void KeyValueEventHandler<K, T>(K key, T value);

	public class StateCallbackStrategy<TState>
		where TState : Schema
	{
		protected Decoder<TState> Decoder;

		protected HashSet<int> UniqueRefIds = new HashSet<int>();

		public StateCallbackStrategy(Decoder<TState> decoder)
		{
			Decoder = decoder;
			Decoder.TriggerChanges = TriggerChanges;
		}

		protected Action AddCallback(int refId, object operationOrProperty, Delegate handler)
		{
			if (!Decoder.Refs.callbacks.TryGetValue(refId, out var handlers))
			{
				handlers = new Dictionary<object, List<Delegate>>();
				Decoder.Refs.callbacks[refId] = handlers;
			}
			if (!handlers.ContainsKey(operationOrProperty))
			{
				handlers[operationOrProperty] = new List<Delegate>();
			}
			handlers[operationOrProperty].Add(handler);
			return () => handlers[operationOrProperty].Remove(handler);
		}

		protected Action AddCallbackOrWaitCollectionAvailable<TInstance, TReturn>(TInstance instance, Expression<Func<TInstance, TReturn>> propertyExpression, OPERATION operation, Delegate handler)
			where TInstance : Schema
			where TReturn : IRef
		{
			var memberExpression = (MemberExpression)propertyExpression.Body;
			var propertyName = memberExpression.Member.Name;

			Action removeHandler = () => {};
			Action removeOnAdd = () => removeHandler();

			// Collection not available yet. Listen for its availability before attaching the handler.
			if (instance[propertyName] == null)
			{
				removeHandler = Listen(instance, propertyExpression, (TReturn array, TReturn _) =>
				{
					removeHandler = AddCallback(array.__refId, operation, handler);
				});
				return removeOnAdd;
			} else {
				return AddCallback(((IRef)instance[propertyName]).__refId, operation, handler);
			}
		}

		public Action Listen<TReturn>(Expression<Func<TState, TReturn>> propertyExpression, PropertyChangeEventHandler<TReturn> handler)
		{
			return Listen(Decoder.State, propertyExpression, handler);
		}

		public Action Listen<TInstance, TReturn>(TInstance instance, Expression<Func<TInstance, TReturn>> propertyExpression, PropertyChangeEventHandler<TReturn> handler)
			where TInstance : Schema
		{
			var memberExpression = (MemberExpression)propertyExpression.Body;
			var propertyName = memberExpression.Member.Name;
			return AddCallback(instance.__refId, propertyName, handler);
		}

		public Action OnChange<T>(T instance, OnChangeEventHandler handler)
			where T : Schema
		{
			return AddCallback(instance.__refId, OPERATION.REPLACE, handler);
		}

		public Action OnAdd<TReturn>(Expression<Func<TState, ArraySchema<TReturn>>> propertyExpression, KeyValueEventHandler<int, TReturn> handler)
		{
			return OnAdd(Decoder.State, propertyExpression, handler);
		}

		public Action OnAdd<TInstance, TReturn>(TInstance instance, Expression<Func<TInstance, ArraySchema<TReturn>>> propertyExpression, KeyValueEventHandler<int, TReturn> handler)
			where TInstance : Schema
		{
			return AddCallbackOrWaitCollectionAvailable(instance, propertyExpression, OPERATION.ADD, handler);
		}

		public Action OnAdd<TReturn>(Expression<Func<TState, MapSchema<TReturn>>> propertyExpression, KeyValueEventHandler<string, TReturn> handler)
		{
			return OnAdd(Decoder.State, propertyExpression, handler);
		}

		public Action OnAdd<TInstance, TReturn>(TInstance instance, Expression<Func<TInstance, MapSchema<TReturn>>> propertyExpression, KeyValueEventHandler<string, TReturn> handler)
			where TInstance : Schema
		{
			return AddCallbackOrWaitCollectionAvailable(instance, propertyExpression, OPERATION.ADD, handler);
		}

		public Action OnRemove<TReturn>(Expression<Func<TState, ArraySchema<TReturn>>> propertyExpression, KeyValueEventHandler<int, TReturn> handler)
		{
			return OnRemove(Decoder.State, propertyExpression, handler);
		}

		public Action OnRemove<TInstance, TReturn>(TInstance instance, Expression<Func<TInstance, ArraySchema<TReturn>>> propertyExpression, KeyValueEventHandler<int, TReturn> handler)
			where TInstance : Schema
		{
			return AddCallbackOrWaitCollectionAvailable(instance, propertyExpression, OPERATION.DELETE, handler);
		}

		public Action OnRemove<TReturn>(Expression<Func<TState, MapSchema<TReturn>>> propertyExpression, KeyValueEventHandler<string, TReturn> handler)
		{
			return OnRemove(Decoder.State, propertyExpression, handler);
		}

		public Action OnRemove<TInstance, TReturn>(TInstance instance, Expression<Func<TInstance, MapSchema<TReturn>>> propertyExpression, KeyValueEventHandler<string, TReturn> handler)
			where TInstance : Schema
		{
			return AddCallbackOrWaitCollectionAvailable(instance, propertyExpression, OPERATION.DELETE, handler);
		}

		// ...

		/// <summary>
		/// 	Binds a schema property to a target object.
		/// </summary>
		/// <param name="instance"></param>
		/// <param name="target"></param>
		/// <returns></returns>
		public Action BindTo<T>(Schema from, T to)
		{
			return AddCallback(from.__refId, to, (Action)(() =>
			{
				var toType = typeof(T);
				foreach (var field in from.fieldsByIndex)
				{
					var fromValue = from.GetType().GetProperty(field.Value)?.GetValue(from);
					var toProperty = toType.GetProperty(field.Value);

					if (toProperty != null && fromValue != null)
					{
						if (toProperty.PropertyType.IsAssignableFrom(fromValue.GetType()))
						{
							toProperty.SetValue(to, fromValue);
						}
						else
						{
							// Handle type mismatch, maybe convert or log
							UnityEngine.Debug.Log($"BindTo: Type mismatch for property {field.Value}: Cannot assign {fromValue.GetType().Name} to {toProperty.PropertyType.Name}");
						}
					}
				}
			}));
		}

		protected void TriggerChanges(ref List<DataChange> allChanges)
		{
			UniqueRefIds.Clear();

			foreach (DataChange change in allChanges)
			{
				var refId = change.RefId;
				var _ref = Decoder.Refs.Get(refId);

				// Dictionary<object, List<Delegate>> callbacks = Decoder.Refs.callbacks[refId];
				Decoder.Refs.callbacks.TryGetValue(refId, out var callbacks);

				if (callbacks == null)
				{
					continue;
				}

				//
				// trigger onRemove on child structure.
				//
				if (
					(change.Op & (byte)OPERATION.DELETE) == (byte)OPERATION.DELETE &&
					change.PreviousValue is Schema
				)
				{
					Decoder.Refs.callbacks.TryGetValue(((Schema)change.PreviousValue).__refId, out var deleteCallbacks);
					if (deleteCallbacks != null && deleteCallbacks[OPERATION.DELETE] != null)
					{
						foreach (var callback in deleteCallbacks[OPERATION.DELETE])
						{
							callback.DynamicInvoke();
						}
					}
				}

				if (_ref is Schema)
				{
					//
					// Handle Schema instance
					//

					if (!UniqueRefIds.Contains(refId))
					{
						// trigger onChange
						callbacks.TryGetValue(OPERATION.REPLACE, out var replaceCallbacks);
						if (replaceCallbacks != null)
						{
							foreach (var callback in replaceCallbacks)
							{
								try
								{
									callback.DynamicInvoke();
								}
								catch (Exception e)
								{
									UnityEngine.Debug.LogError(e.Message);
								}
							}
						}
					}

					callbacks.TryGetValue(change.Field, out var fieldCallbacks);
					if (fieldCallbacks != null)
					{
						foreach (var callback in fieldCallbacks)
						{
							try
							{
								callback.DynamicInvoke(change.Value, change.PreviousValue);
							}
							catch (Exception e)
							{
								UnityEngine.Debug.LogError(e.Message);
							}
						}
					}
				}
				else
				{
					//
					// Handle collection of items
					//
					ISchemaCollection container = (ISchemaCollection)_ref;

					if ((change.Op & (byte)OPERATION.DELETE) == (byte)OPERATION.DELETE)
					{
						if (change.PreviousValue != container.GetTypeDefaultValue())
						{
							// trigger onRemove
							callbacks.TryGetValue(OPERATION.DELETE, out var deleteCallbacks);
							if (deleteCallbacks != null)
							{
								foreach (var callback in deleteCallbacks)
								{
									callback.DynamicInvoke(change.DynamicIndex ?? change.Field, change.PreviousValue);
								}
							}
						}

						// Handle DELETE_AND_ADD operation
						if ((change.Op & (byte)OPERATION.ADD) == (byte)OPERATION.ADD)
						{
							// trigger onRemove
							callbacks.TryGetValue(OPERATION.ADD, out var addCallbacks);
							if (addCallbacks != null)
							{
								foreach (var callback in addCallbacks)
								{
									callback.DynamicInvoke(change.DynamicIndex, change.Value);
								}
							}
						}

					}
					else if (
						(change.Op & (byte)OPERATION.ADD) == (byte)OPERATION.ADD &&
						(
							change.PreviousValue == null ||
							Equals(change.PreviousValue, container.GetTypeDefaultValue())
						)
					)
					{
						// trigger onAdd
						callbacks.TryGetValue(OPERATION.ADD, out var addCallbacks);
						if (addCallbacks != null)
						{
							foreach (var callback in addCallbacks)
							{
								callback.DynamicInvoke(change.DynamicIndex ?? change.Field, change.Value);
							}
						}
					}

					// trigger onChange
					if (change.Value != change.PreviousValue && change.Value != null)
					{
						callbacks.TryGetValue(OPERATION.REPLACE, out var replaceCallbacks);
						if (replaceCallbacks != null)
						{
							foreach (var callback in replaceCallbacks)
							{
								callback.DynamicInvoke(change.DynamicIndex ?? change.Field, change.Value);
							}
						}
					}
				}

				UniqueRefIds.Add(refId);
			}
		}
	}

	public class Callbacks // <T> where T : Schema
	{
		public static StateCallbackStrategy<T> Get<T>(ColyseusRoom<T> room)
			where T : Schema
		{
			var decoder = (room.Serializer as ColyseusSchemaSerializer<T>).Decoder;
			return new StateCallbackStrategy<T>(decoder);
		}

		public static StateCallbackStrategy<T> Get<T>(Decoder<T> decoder)
			where T : Schema
		{
			return new StateCallbackStrategy<T>(decoder);
		}

		internal static void RemoveChildRefs(ISchemaCollection collection, ref List<DataChange> changes, ref ColyseusReferenceTracker refs)
		{
			if (refs == null) {
				return;
			}

			foreach (DictionaryEntry item in collection.GetItems())
			{
				changes.Add(new DataChange
				{
					RefId = collection.__refId,
					Op = (byte)OPERATION.DELETE,
					//Field = item.Key,
					DynamicIndex = item.Key,
					Value = null,
					PreviousValue = item.Value
				});

				if (collection.HasSchemaChild)
				{
					refs.Remove((item.Value as IRef).__refId);
				}
			}
		}
	}
}
