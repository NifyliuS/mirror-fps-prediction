#define onlySyncOnChange_BANDWIDTH_SAVING
using System;
using System.Collections.Generic;
using System.Collections;
using UnityEngine;
using Mirror;
using UnityEngine.Animations;

/*
    Documentation: https://mirror-networking.gitbook.io/docs/components/network-transform
    API Reference: https://mirror-networking.com/docs/api/Mirror.NetworkTransformBase.html
*/
namespace NetworkScripts {
  public class DynamicNetworkTransform : NetworkTransformBase {
    protected override Transform       targetComponent        => transform;
    public             NetworkIdentity ParentNetworkIdentity  => _parentIdentity;
    public             Transform       ParentNetworkTransform => _parentIdentity ? _parentIdentity.transform : null;

    public bool syncVirtualParent = true;

    private NetworkIdentity _parentIdentity;
    private bool            _isParentActive = false;
    private Vector3         _parentPosition;
    private Quaternion      _parentRotation;
    private Vector3         _parentScale;

    [SerializeField] private Nullable<Vector3>    _positionOffset = null;
    [SerializeField] private Nullable<Quaternion> _rotationOffset = null;
    [SerializeField] private Nullable<Vector3>    _scaleOffset    = null;

  #region Initialization

    private IEnumerator ResolveIdentity(uint newNetId) {
      while (syncVirtualParent && !_parentIdentity && netId != 0) {
        if (NetworkClient.spawned.TryGetValue(newNetId, out _parentIdentity)) ;
        yield return null;
      }
    }

    public override bool OnSerialize(NetworkWriter writer, bool initialState) {
      if (initialState && syncVirtualParent) writer.WriteUInt(_parentIdentity ? _parentIdentity.netId : (uint) 0);
      return base.OnSerialize(writer, initialState);
    }

    public override void OnDeserialize(NetworkReader reader, bool initialState) {
      //pass the netId, and then on client you'd need a coroutine to keep checking NetworkClient.spawned until it's there inside a loop with a yield return null
      if (initialState && syncVirtualParent) {
        uint netId = reader.ReadUInt();
        StartCoroutine(ResolveIdentity(netId));
      }

      base.OnDeserialize(reader, initialState);
    }

  #endregion

  #region Client RPCs

    [ClientRpc(channel = Channels.Reliable)] //We want to make sure the NETID is sent since it is a one-off event
    public void RpcUpdateNetworkParent(NetworkIdentity identity) {
      _parentIdentity = identity;
    }

    [ClientRpc(channel = Channels.Reliable)] //We want to make sure the NETID is sent since it is a one-off event
    public void RpcClearNetworkParent() {
      _parentIdentity = null;
    }

    [ClientRpc(channel = Channels.Unreliable)]
    void RpcServerToClientParentOffsetSync(Vector3? position, Quaternion? rotation, Vector3? scale) {
      SetParentOffset(position, rotation, scale);
    }

  #endregion

  #region Server CMDs

    [Command(channel = Channels.Reliable)]
    public void CmdSetNetworkParent(NetworkIdentity identity) {
      //TODO: add client side parent setting
    }

    [Command(channel = Channels.Reliable)]
    public void CmdUnSetNetworkParent(NetworkIdentity identity) {
      //TODO: add client side parent setting
    }

    [Command(channel = Channels.Unreliable)]
    void CmdClientToServerParentOffsetSync(Vector3? position, Quaternion? rotation, Vector3? scale) {
      //TODO: Add client authority integration
    }

  #endregion

  #region Server

  #endregion


  #region Client

  #endregion

  #region Server + Client

    public void UpdateParentOffset() {
      if (_parentIdentity) {
        SetParentOffset(
          targetComponent.position - _parentIdentity.transform.position,
          targetComponent.rotation * Quaternion.Inverse(_parentIdentity.transform.rotation),
          targetComponent.localScale - _parentIdentity.transform.localScale
        );
      }
    }

    public void SetNetworkTransformParent(NetworkIdentity identity) {
      _parentIdentity = identity;
      RpcUpdateNetworkParent(identity);
      UpdateParentOffset();
    }

    public void UnSetNetworkTransformParent() {
      _parentIdentity = null;
      RpcClearNetworkParent();
    }

    public void SetParentOffset(Vector3? position, Quaternion? rotation, Vector3? scale) {
      _positionOffset = position;
      _rotationOffset = rotation;
      _scaleOffset = scale;
    }

  #endregion


    /* Called by RpcServerToClientSync() */
    protected override void OnServerToClientSync(Vector3? position, Quaternion? rotation, Vector3? scale) {
      if (_parentIdentity) {
        if (isServer) {
          UpdateParentOffset();
          RpcServerToClientParentOffsetSync(_positionOffset, _rotationOffset, _scaleOffset);
        }


        if (!_isParentActive && _positionOffset.HasValue && !isServer) {
          Vector3 _localOffset = targetComponent.position - _parentIdentity.transform.position;
          OnTeleport((Vector3) _localOffset);
          targetComponent.position = targetComponent.position + _localOffset;
          targetComponent.localPosition = _parentIdentity.transform.position + _localOffset;
          base.OnServerToClientSync(
            _localOffset,
            rotation,
            scale
          );
          _isParentActive = true;
        }
        else {
          base.OnServerToClientSync(
            _positionOffset,
            rotation,
            scale
          );
        }
      }
      else {
        if (_isParentActive) {
          if (position.HasValue) {
            OnTeleport((Vector3) position);
            targetComponent.position = (Vector3) position;
            targetComponent.localPosition = (Vector3) position;
          }

          base.OnServerToClientSync(
            position,
            rotation,
            scale
          );
          _isParentActive = false;
        }

        base.OnServerToClientSync(position, rotation, scale);
      }
    }


  #region Unity Callbacks

    private (Vector3, Quaternion, Vector3) GetParentPosition() {
      if (_parentIdentity) {
        _parentPosition = _parentIdentity.transform.position;
        _parentRotation = _parentIdentity.transform.rotation;
        _parentScale = _parentIdentity.transform.localScale;
      }

      return (_parentPosition, _parentRotation, _parentScale);
    }

    protected override void ApplySnapshot(NTSnapshot start, NTSnapshot goal, NTSnapshot interpolated) {
      if (_isParentActive) {
        var (position, rotation, scale) = GetParentPosition();
        interpolated.position = rotation * interpolated.position + position;
        interpolated.rotation *= rotation;
        interpolated.scale = Vector3.Scale(scale, interpolated.scale);

        goal.position = rotation * (goal.position) + position;
        goal.rotation *= rotation;
        goal.scale = Vector3.Scale(scale, goal.scale);
      }

      base.ApplySnapshot(start, goal, interpolated);
    }


    protected override void OnValidate() {
      base.OnValidate();
    }

    /// <summary>
    /// This calls Reset()
    /// </summary>
    protected override void OnEnable() {
      base.OnEnable();
    }

    /// <summary>
    /// This calls Reset()
    /// </summary>
    protected override void OnDisable() {
      base.OnDisable();
    }

  #endregion
  }
}