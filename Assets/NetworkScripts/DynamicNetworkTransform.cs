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
  public struct NtPositionPack {
    public Vector3?    Position;
    public Quaternion? Rotation;
    public Vector3?    Scale;
  }

  public class DynamicNetworkTransform : NetworkTransformBase {
    protected override Transform       targetComponent        => transform;
    public             NetworkIdentity ParentNetworkIdentity  => _parentIdentity;
    public             Transform       ParentNetworkTransform => _parentIdentity ? _parentIdentity.transform : null;

    public bool syncVirtualParent = true;

    public  NetworkIdentity _parentIdentity;
    private bool            _isParentActive = false;
    private Vector3         _parentPosition;
    private Quaternion      _parentRotation;
    private Vector3         _parentScale;
    private Vector3         _originalScale;
    private bool            _isParentChange = false;

    private Nullable<Vector3>    _positionOffset = null;
    private Nullable<Quaternion> _rotationOffset = null;
    private Nullable<Vector3>    _scaleOffset    = null;

    private Vector3    _oldParentPosition;
    private Quaternion _oldParentRotation;
    private Vector3    _oldParentScale;

  #region Initialization

    private IEnumerator ResolveIdentity(uint newNetId) {
      while (syncVirtualParent && !_parentIdentity && netId != 0) {
        if (NetworkClient.spawned.TryGetValue(newNetId, out _parentIdentity)) ;
        _originalScale = targetComponent.localScale;
        if (_parentIdentity) _isParentActive = true;
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
    public void RpcUpdateNetworkParent(NetworkIdentity identity, Vector3? position, Quaternion? rotation, Vector3? scale) {
      SetParentOffset(position, rotation, scale);
      _isParentChange = _parentIdentity && _parentIdentity.netId != identity.netId;
      if (_isParentChange) ChangeParent(identity);
      else _parentIdentity = identity;
    }

    [ClientRpc(channel = Channels.Reliable)] //We want to make sure the NETID is sent since it is a one-off event
    public void RpcClearNetworkParent() {
      _parentIdentity = null;
    }

    [ClientRpc(channel = Channels.Reliable)] //We want to make sure the NETID is sent since it is a one-off event
    public void RpcDeactivateNetworkParent(uint parentNetId) {
      if (_parentIdentity && _parentIdentity.netId == parentNetId) {
        _parentIdentity = null;
      }
    }

    [ClientRpc(channel = Channels.Unreliable)]
    void RpcServerToClientParentOffsetSync(Vector3? position, Quaternion? rotation, Vector3? scale, uint parentNetId) {
      if (parentNetId == 0 || _parentIdentity.netId != parentNetId) return; //Disallow mixing offset positions from different parents - may happen due to using "Unreliable" channel
      SetParentOffset(position, rotation, scale);
    }

  #endregion


  #region Parent Handling Functions

    private void ActivateParent() {
      _isParentActive = true;
      _originalScale = targetComponent.localScale;
      var identityTransform = _parentIdentity.transform;
      AdjustTransformPosition(
        new NtPositionPack() {
          Position = targetComponent.localPosition - identityTransform.position,
          Rotation = targetComponent.localRotation * Quaternion.Inverse(identityTransform.rotation),
          Scale = GetScaleOffset(targetComponent.localScale, identityTransform.localScale)
        },
        new NtPositionPack()
      );
    }

    private void DeactivateParent() {
      _isParentActive = false;
      AdjustTransformPosition(
        new NtPositionPack() {
          Position = targetComponent.localPosition,
          Rotation = targetComponent.localRotation,
          Scale = _originalScale
        },
        new NtPositionPack()
      );
    }

    private void ChangeParent(NetworkIdentity newIdentity) {
      _isParentChange = false;
      var newTransform = newIdentity.transform;
      AdjustTransformPosition(
        new NtPositionPack() {
          Position = targetComponent.localPosition - newTransform.position,
          Rotation = targetComponent.localRotation * Quaternion.Inverse(newTransform.rotation),
          Scale = GetScaleOffset(targetComponent.localScale, newTransform.localScale)
        },
        new NtPositionPack()
      );
      _parentIdentity = newIdentity;
    }

    private void AdjustTransformPosition(NtPositionPack startPack, NtPositionPack goalPack) {
      Reset();
      base.OnServerToClientSync(
        startPack.Position,
        startPack.Rotation,
        startPack.Scale
      );
      // TODO: Add both start and goal buffer positions rewrite
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

    [Server]
    private void UpdateServerOffsetState() {
      if (_parentIdentity) {
        UpdateParentOffset();
        RpcServerToClientParentOffsetSync(_positionOffset, _rotationOffset, _scaleOffset, _parentIdentity.netId > 0 ? _parentIdentity.netId : 0);
      }
    }

  #endregion


  #region Client

  #endregion

  #region Server + Client

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
      if (_parentIdentity && _parentIdentity.netId == identity.netId) return;
      _parentIdentity = identity;
      UpdateParentOffset();
      RpcUpdateNetworkParent(identity, _positionOffset, _rotationOffset, _scaleOffset);
    }

    public void UnSetNetworkTransformParent() {
      if (_parentIdentity) {
        _parentIdentity = null;
        RpcClearNetworkParent();
      }
    }

    public void DeactivateNetworkTransformParent(NetworkIdentity identity) {
      if (_parentIdentity && _parentIdentity.netId == identity.netId) {
        _parentIdentity = null;
        RpcDeactivateNetworkParent(identity.netId);
      }
    }

    private void SetParentOffset(Vector3? position, Quaternion? rotation, Vector3? scale) {
      _positionOffset = position;
      _rotationOffset = rotation;
      _scaleOffset = scale;
    }

  #endregion


    /* Called by RpcServerToClientSync() */
    protected override void OnServerToClientSync(Vector3? position, Quaternion? rotation, Vector3? scale) {
      if (isServer) {
        UpdateServerOffsetState();
      }

      if (!_parentIdentity) {
        if (!_isParentActive) base.OnServerToClientSync(position, rotation, scale);
        else DeactivateParent();
      }
      else {
        if (_isParentActive) base.OnServerToClientSync(_positionOffset, _rotationOffset, _scaleOffset);
        else ActivateParent();
      }
    }


  #region Unity Callbacks

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

    protected override void ApplySnapshot(NTSnapshot start, NTSnapshot goal, NTSnapshot interpolated) {
      if (_isParentActive) {
        var (position, rotation, scale) = GetParentOffsetPosition();
        interpolated.position = rotation * interpolated.position + position;
        interpolated.rotation *= rotation;
        interpolated.scale = Vector3.Scale(scale, interpolated.scale);

        goal.position = rotation * (goal.position) + position;
        goal.rotation *= rotation;
        goal.scale = Vector3.Scale(scale, goal.scale);
      }

      base.ApplySnapshot(start, goal, interpolated);
    }

  #endregion

  #region Helper Functions

    private (Vector3, Quaternion, Vector3) GetParentOffsetPosition() {
      if (_parentIdentity) {
        var parentTransform = _parentIdentity.transform;
        _parentPosition = parentTransform.position;
        _parentRotation = parentTransform.rotation;
        _parentScale = parentTransform.localScale;
      }

      return (_parentPosition, _parentRotation, _parentScale);
    }

    private Vector3 GetScaleOffset(Vector3 targetScale, Vector3 parentScale) {
      return new Vector3(
        targetScale.x / parentScale.x,
        targetScale.y / parentScale.y,
        targetScale.z / parentScale.z
      );
    }

  #endregion
  }
}