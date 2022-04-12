using System;
using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace PlayerScripts {
  public class NetworkPlayerController : NetworkBehaviour {
  #region Private State
    // Private state variables are set here
    private bool _isServerLocalPlayer  = false;
    private bool _isServerRemotePlayer = false;
    private bool _isClientLocalPlayer  = false;
    private bool _isClientRemotePlayer  = false;
  #endregion

    private void Awake() {
      // For ease of use later i am setting the variables we will need to differentiate the 4 cases of players
      _isServerLocalPlayer = isServer && isLocalPlayer;
      _isServerRemotePlayer = !_isServerLocalPlayer;
      _isClientLocalPlayer = isClient && isLocalPlayer;
      _isClientRemotePlayer = !_isClientLocalPlayer;
    }
    
  }
}