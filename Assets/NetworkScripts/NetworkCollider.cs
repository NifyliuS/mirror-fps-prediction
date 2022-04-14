using System;
using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

public class NetworkCollider : NetworkBehaviour {
  private void OnCollisionEnter(Collision other) {
    //Debug.Log("NetworkCollider -> OnCollisionEnter");
  }

  private void OnTriggerEnter(Collider other) {
    // Debug.Log("NetworkCollider -> OnTriggerEnter");
  }
}