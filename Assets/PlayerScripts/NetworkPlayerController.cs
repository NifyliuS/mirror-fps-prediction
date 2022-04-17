using System;
using System.Collections;
using System.Collections.Generic;
using Mirror;
using NetworkScripts;
using UnityEngine;

namespace PlayerScripts {
  public class NetworkPlayerController : NetworkBehaviour {
  #region Private State

    // Private state variables are set here
    private bool       _isServerLocalPlayer  = false;
    private bool       _isServerRemotePlayer = false;
    private bool       _isClientLocalPlayer  = false;
    private bool       _isClientRemotePlayer = false;
    private GameObject _LocalPlayer;
    private GameObject _RemotePlayer;

    private List<NetworkIdentity> _parents = new List<NetworkIdentity>();
    private NetworkIdentity       _activeParent;

  #endregion

  #region Public variables

    public GameObject              CharacterPrefab;
    public DynamicNetworkTransform DynamicNT;

  #endregion

    private void InstantiateCharacters() {
      if (isLocalPlayer) {
        _LocalPlayer = Instantiate(CharacterPrefab, transform.position, transform.rotation);
        _LocalPlayer.transform.SetParent(null);
      }

      _RemotePlayer = Instantiate(CharacterPrefab, transform.position, transform.rotation);
      _RemotePlayer.transform.SetParent(transform);
    }

    private void Awake() {
      // For ease of use later i am setting the variables we will need to differentiate the 4 cases of players
      _isServerLocalPlayer = isServer && isLocalPlayer;
      _isServerRemotePlayer = !_isServerLocalPlayer;
      _isClientLocalPlayer = isClient && isLocalPlayer;
      _isClientRemotePlayer = !_isClientLocalPlayer;
      InstantiateCharacters();
    }

    [Command]
    private void CmdSetServerPosition(Vector3 newPosition) {
      transform.position = newPosition;
    }


    private void FixedUpdate() {
      if (!hasAuthority) return;
      if (Input.GetKey(KeyCode.Q)) {
        transform.rotation *= Quaternion.Euler(0.5f, 0, 0);
      }

      if (Input.GetKey(KeyCode.E)) {
        transform.rotation *= Quaternion.Euler(-0.5f, 0, 0);
      }

      if (Input.GetKey(KeyCode.W)) {
        transform.position = new Vector3(transform.position.x, transform.position.y + 0.05f, transform.position.z);
      }

      if (Input.GetKey(KeyCode.S)) {
        transform.position = new Vector3(transform.position.x, transform.position.y - 0.05f, transform.position.z);
      }

      if (Input.GetKey(KeyCode.D)) {
        transform.position = new Vector3(transform.position.x + 0.05f, transform.position.y, transform.position.z);
      }

      if (Input.GetKey(KeyCode.A)) {
        transform.position = new Vector3(transform.position.x - 0.05f, transform.position.y, transform.position.z);
      }
    }

    private void OnTriggerEnter(Collider collider) {
      NetworkCollider networkCollider = collider.GetComponentInParent<NetworkCollider>();
      if (networkCollider && hasAuthority) {
        Debug.Log("Set Network Parent Identity");
        _parents.Add(networkCollider.netIdentity);
        _activeParent = networkCollider.netIdentity;
        DynamicNT.SetNetworkTransformParent(networkCollider.netIdentity);
      }
    }


    private void OnTriggerExit(Collider collider) {
      NetworkCollider networkCollider = collider.GetComponentInParent<NetworkCollider>();
      if (networkCollider && hasAuthority) {
        Debug.Log("Clear/Change Network Parent Identity");
        if (networkCollider.netIdentity.netId == _activeParent.netId) {
          _parents.RemoveAt(_parents.Count - 1);
        }
        else {
          List<NetworkIdentity> filteredParents = new List<NetworkIdentity>();
          foreach (NetworkIdentity NID in _parents) {
            if (NID.netId != networkCollider.netIdentity.netId) {
              filteredParents.Add(NID);
            }
          }

          _parents = filteredParents;
        }

        if (_parents.Count == 0) {
          DynamicNT.UnSetNetworkTransformParent();
        }
        else {
          DynamicNT.SetNetworkTransformParent(_parents[_parents.Count - 1]);
        }
      }
    }
  }
}