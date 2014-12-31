﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Network;

namespace UNet
{
	public sealed class USocket: IDisposable
	{
		private IntPtr peerPtr = IntPtr.Zero;
		private readonly EService service;
		private readonly LinkedList<byte[]> recvBuffer = new LinkedList<byte[]>();

		public Action<EEvent> Connected { get; set; }
		public Action<EEvent> Received { get; set; }
		public Action<EEvent> Disconnect { get; set; }
		public Action<int> Error { get; set; }

		public USocket(EService service)
		{
			this.service = service;
		}

		private void Dispose(bool disposing)
		{
			if (this.peerPtr == IntPtr.Zero)
			{
				return;
			}

			NativeMethods.EnetPeerReset(this.peerPtr);
			this.peerPtr = IntPtr.Zero;
		}

		~USocket()
		{
			Dispose(false);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		public IntPtr PeerPtr
		{
			get
			{
				return this.peerPtr;
			}
			set
			{
				this.peerPtr = value;
			}
		}
		
		private ENetPeer Struct
		{
			get
			{
				if (this.peerPtr == IntPtr.Zero)
				{
					return new ENetPeer();
				}
				ENetPeer peer = (ENetPeer) Marshal.PtrToStructure(this.peerPtr, typeof (ENetPeer));
				return peer;
			}
			set
			{
				Marshal.StructureToPtr(value, this.peerPtr, false);
			}
		}

		public string RemoteAddress
		{
			get
			{
				ENetPeer peer = this.Struct;
				return peer.Address.Host + ":" + peer.Address.Port;
			}
		}

		public void Ping()
		{
			NativeMethods.EnetPeerPing(this.peerPtr);
		}

		public void ConfigureThrottle(uint interval, uint acceleration, uint deceleration)
		{
			NativeMethods.EnetPeerThrottleConfigure(this.peerPtr, interval, acceleration, deceleration);
		}

		public Task<bool> ConnectAsync(
				string hostName, ushort port,
				uint channelLimit = NativeMethods.ENET_PROTOCOL_MAXIMUM_CHANNEL_COUNT, uint data = 0)
		{
			if (channelLimit > NativeMethods.ENET_PROTOCOL_MAXIMUM_CHANNEL_COUNT)
			{
				throw new ArgumentOutOfRangeException("channelLimit");
			}

			var tcs = new TaskCompletionSource<bool>();
			Address address = new Address { HostName = hostName, Port = port };
			ENetAddress nativeAddress = address.Struct;
			this.peerPtr = NativeMethods.EnetHostConnect(this.service.HostPtr, ref nativeAddress,
					channelLimit, data);
			if (this.peerPtr == IntPtr.Zero)
			{
				throw new EException("host connect call failed.");
			}
			this.service.PeersManager.Add(this.peerPtr, this);
			this.Connected = eEvent =>
			{
				if (eEvent.EventState == EventState.DISCONNECTED)
				{
					tcs.TrySetException(new EException("socket disconnected in connect"));
				}
				tcs.TrySetResult(true);
			};
			return tcs.Task;
		}

		public Task<bool> AcceptAsync()
		{
			if (this.service.PeersManager.ContainsKey(IntPtr.Zero))
			{
				throw new EException("do not accept twice!");
			}

			var tcs = new TaskCompletionSource<bool>();

			// 如果有请求连接缓存的包,从缓存中取
			if (this.service.ConnEEvents.Count > 0)
			{
				EEvent eEvent = this.service.ConnEEvents.First.Value;
				this.service.ConnEEvents.RemoveFirst();

				this.PeerPtr = eEvent.PeerPtr;
				this.service.PeersManager.Add(this.PeerPtr, this);
				tcs.TrySetResult(true);
			}
			else
			{
				this.service.PeersManager.Add(this.PeerPtr, this);
				this.Connected = eEvent =>
				{
					if (eEvent.EventState == EventState.DISCONNECTED)
					{
						tcs.TrySetException(new EException("socket disconnected in accpet"));
					}

					this.service.PeersManager.Remove(IntPtr.Zero);

					this.PeerPtr = eEvent.PeerPtr;
					this.service.PeersManager.Add(this.PeerPtr, this);
					tcs.TrySetResult(true);
				};
			}
			return tcs.Task;
		}

		public void WriteAsync(byte[] data, byte channelID = 0, PacketFlags flags = PacketFlags.Reliable)
		{
			var packet = new EPacket(data, flags);
			NativeMethods.EnetPeerSend(this.peerPtr, channelID, packet.PacketPtr);
			// enet_peer_send函数会自动删除packet,设置为0,防止Dispose或者析构函数再次删除
			packet.PacketPtr = IntPtr.Zero;
		}

		public Task<byte[]> ReadAsync()
		{
			var tcs = new TaskCompletionSource<byte[]>();

			// 如果有缓存的包,从缓存中取
			if (this.recvBuffer.Count > 0)
			{
				byte[] bytes = this.recvBuffer.First.Value;
				this.recvBuffer.RemoveFirst();
				tcs.TrySetResult(bytes);
			}
			// 没有缓存封包,设置回调等待
			else
			{
				this.Received = eEvent =>
				{
					if (eEvent.EventState == EventState.DISCONNECTED)
					{
						tcs.TrySetException(new EException("socket disconnected in receive"));
					}

					using (EPacket packet = new EPacket(eEvent.PacketPtr))
					{
						byte[] bytes = packet.Bytes;
						tcs.TrySetResult(bytes);
					}
				};
			}
			return tcs.Task;
		}

		public Task<bool> DisconnectAsync(uint data = 0)
		{
			NativeMethods.EnetPeerDisconnect(this.peerPtr, data);
			// EnetPeerDisconnect会reset Peer,这里设置为0,防止再次Dispose
			this.PeerPtr = IntPtr.Zero;
			var tcs = new TaskCompletionSource<bool>();
			this.Disconnect = eEvent => tcs.TrySetResult(true);
			return tcs.Task;
		}

		public Task<bool> DisconnectLaterAsync(uint data = 0)
		{
			NativeMethods.EnetPeerDisconnectLater(this.peerPtr, data);
			// EnetPeerDisconnect会reset Peer,这里设置为0,防止再次Dispose
			this.PeerPtr = IntPtr.Zero;
			var tcs = new TaskCompletionSource<bool>();
			this.Disconnect = eEvent => tcs.TrySetResult(true);
			return tcs.Task;
		}

		public void DisconnectNow(uint data)
		{
			NativeMethods.EnetPeerDisconnectNow(this.peerPtr, data);
			// EnetPeerDisconnect会reset Peer,这里设置为0,防止再次Dispose
			this.PeerPtr = IntPtr.Zero;
		}

		internal void OnConnected(EEvent eEvent)
		{
			if (this.Connected == null)
			{
				return;
			}
			Action<EEvent> localConnected = this.Connected;
			this.Connected = null;
			// 此调用将让await ConnectAsync返回,所以null必须在此之前设置
			localConnected(eEvent);
		}

		internal void OnReceived(EEvent eEvent)
		{
			// 如果应用层还未调用readasync则将包放到缓存队列
			if (this.Received == null)
			{
				using (var packet = new EPacket(eEvent.PacketPtr))
				{
					var bytes = packet.Bytes;
					this.recvBuffer.AddLast(bytes);
				}
			}
			else
			{
				Action<EEvent> localReceived = this.Received;
				this.Received = null;
				// 此调用将让await ReadAsync返回,所以null必须在此之前设置
				localReceived(eEvent);
			}
		}

		internal void OnDisconnect(EEvent eEvent)
		{
			if (this.Disconnect == null)
			{
				return;
			}
			this.Disconnect(eEvent);
		}

		internal void OnError(int errorCode)
		{
			if (this.Error == null)
			{
				return;
			}
			this.Error(errorCode);
		}
	}
}