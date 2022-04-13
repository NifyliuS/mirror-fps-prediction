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
  #endregion
  #region Public variables
    public GameObject              CharacterPrefab;
    public DynamicNetworkTransform DynamicNT;
  #endregion

    private void InstantiateCharacters() {
      if (isLocalPlayer) {
        _LocalPlayer = Instantiate(CharacterPrefab, new Vector3(transform.position.x, transform.position.y, transform.position.z), Quaternion.identity);
        _LocalPlayer.transform.SetParent(null); 
      }
       
      _RemotePlayer = Instantiate(CharacterPrefab, new Vector3(transform.position.x, transform.position.y, transform.position.z), Quaternion.identity);
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


    private void Update() {
      if (Input.GetKey(KeyCode.W)) {
        transform.position = new Vector3(transform.position.x, transform.position.y + 0.01f, transform.position.z);
      }
      if (Input.GetKey(KeyCode.S)) {
        transform.position = new Vector3(transform.position.x, transform.position.y - 0.01f, transform.position.z);
      }
      
      if (Input.GetKey(KeyCode.D)) {
        transform.position = new Vector3(transform.position.x + 0.01f, transform.position.y , transform.position.z);
      }
      if (Input.GetKey(KeyCode.A)) {
        transform.position = new Vector3(transform.position.x - 0.01f, transform.position.y , transform.position.z);
      }
    }

 
    
    [Server]
    private void OnTriggerEnter(Collider collider) {
      Debug.Log("OnTriggerEnterOnTriggerEnterOnTriggerEnterOnTriggerEnterOnTriggerEnterOnTriggerEnterOnTriggerEnter");
      NetworkCollider networkCollider = collider.GetComponentInParent<NetworkCollider>();
      if (networkCollider) {
        Debug.Log("SetNetworkTransformParent");
        DynamicNT.SetNetworkTransformParent(networkCollider.netIdentity);
      }
    }
    [Server]
    private void OnTriggerExit(Collider collider) {
      Debug.Log("OnTriggerExitOnTriggerExitOnTriggerExitOnTriggerExitOnTriggerExitOnTriggerExitOnTriggerExit");
      NetworkCollider networkCollider = collider.GetComponentInParent<NetworkCollider>();
      if (networkCollider) {
        DynamicNT.UnSetNetworkTransformParent();
      }
    }
  }
}