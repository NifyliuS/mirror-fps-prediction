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

    private Nullable<Vector3>    _positionOffsetDefer       = null;
    private Nullable<Quaternion> _rotationOffsetDefer       = null;
    private Nullable<Vector3>    _scaleOffsetDefer          = null;
    private bool                 _deferServerToClientQueued = false;

  #region Initialization

    private void ApplyInitialTransformState() {
      Reset();
      Debug.Log(_rotationOffset);
      //if (_positionOffset.HasValue) targetComponent.position = (Vector3) _positionOffset;
      //if (_rotationOffset.HasValue) targetComponent.rotation = (Quaternion) _rotationOffset;
      //if (_scaleOffset.HasValue) targetComponent.localScale = (Vector3) _scaleOffset;
      // base.OnServerToClientSync(
      //   targetComponent.position,
      //   targetComponent.rotation,
      //   targetComponent.localScale
      // );
    }

    private IEnumerator ResolveIdentity(uint newNetId) {
      while (syncVirtualParent && !_parentIdentity && netId != 0) {
        if (NetworkClient.spawned.TryGetValue(newNetId, out _parentIdentity)) ;
        if (_parentIdentity) _isParentActive = true;
        ;
        yield return null;
      }
    }

    public override bool OnSerialize(NetworkWriter writer, bool initialState) {
      base.OnSerialize(writer, initialState);
      if (initialState) {
        if (syncPosition) writer.WriteVector3(_positionOffset.HasValue ? (Vector3) _positionOffset : new Vector3(0, 0, 0));
        if (syncRotation) writer.WriteQuaternion(_rotationOffset.HasValue ? (Quaternion) _rotationOffset : new Quaternion());
        if (syncScale) writer.WriteVector3(_scaleOffset.HasValue ? (Vector3) _scaleOffset : new Vector3(1, 1, 1));
        if (syncVirtualParent) writer.WriteUInt(_parentIdentity ? _parentIdentity.netId : (uint) 0);
        return true;
      }

      return false;
    }

    public override void OnDeserialize(NetworkReader reader, bool initialState) {
      //pass the netId, and then on client you'd need a coroutine to keep checking NetworkClient.spawned until it's there inside a loop with a yield return null
      base.OnDeserialize(reader, initialState);

      if (initialState) {
        if (syncPosition) _positionOffset = reader.ReadVector3();
        if (syncRotation) _rotationOffset = reader.ReadQuaternion();
        if (syncScale) _scaleOffset = reader.ReadVector3();
        if (syncVirtualParent) StartCoroutine(ResolveIdentity(reader.ReadUInt()));
      }
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

    private Vector3 GetScaleOffset(Vector3 targetScale, Vector3 parentScale) {
      return new Vector3(
        targetScale.x / parentScale.x,
        targetScale.y / parentScale.y,
        targetScale.z / parentScale.z
      );
    }

    public void UpdateParentOffset() {
      if (_parentIdentity) {
        SetParentOffset(
          targetComponent.position - _parentIdentity.transform.position,
          targetComponent.rotation * Quaternion.Inverse(_parentIdentity.transform.rotation),
          GetScaleOffset(targetComponent.localScale, _parentIdentity.transform.localScale)
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


    private void DeferServerToClientSyncFrame(Vector3? position, Quaternion? rotation, Vector3? scale) {
      _positionOffsetDefer = position;
      _rotationOffsetDefer = rotation;
      _scaleOffsetDefer = scale;
      _deferServerToClientQueued = true;
    }

    private void ClearDeferServerToClientSyncFrame() {
      _positionOffsetDefer = null;
      _rotationOffsetDefer = null;
      _scaleOffsetDefer = null;
      _deferServerToClientQueued = false;
    }

    private void LateUpdate() {
      if (_deferServerToClientQueued) {
        base.OnServerToClientSync(
          _positionOffsetDefer,
          _rotationOffsetDefer,
          _scaleOffsetDefer
        ); 
      }
      ClearDeferServerToClientSyncFrame();
    }

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
            _rotationOffset,
            scale
          );
          _isParentActive = true;
          // DeferServerToClientSyncFrame(
          //   _positionOffset,
          //   _rotationOffset,
          //   _scaleOffset
          // );
        }
        else {
          base.OnServerToClientSync(
            _positionOffset,
            _rotationOffset,
            _scaleOffset
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