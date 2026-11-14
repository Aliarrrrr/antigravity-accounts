'use strict';

// Reviewed program identities, original instruction bytes, activity format and
// public OAuth locations form one contract. Consumers derive their accepted
// and rejected program sets here; unknown images are never guessed compatible.
const profiles = [
  {
    "version": "2.32.1",
    "originalSha256": "65cdb8d132fc61056625081be7d07d4d22f57eba485de7edf0d24d98e77c891f",
    "diagnosticsSha256": "8ab537e29e484b7f31e97d1070e4726bc2e4d45180e7afa11d683057d8a5e693",
    "projectSha256": "9728f6eb169fd63a7b154934c7151932152fcc93ca97720117237218df907490",
    "providerSha256": "319c1abd18b4d12ac2149473f04b07be8671d230ecf12adf4cb7d1e3fe85edf6",
    "activityProtocol": "execution-status",
    "diagnostics": {
      "routeAddress": 5430566871n,
      "changes": [
        {
          "address": 5424022082n,
          "before": "0f84d4000000",
          "after": "e93500000090",
          "purpose": "goroutine-only route entry"
        },
        {
          "address": 5424022148n,
          "before": "488d1daf905900",
          "after": "488d1d4cdd6300",
          "purpose": "exact goroutine route literal"
        },
        {
          "address": 5424022155n,
          "before": "b90d000000",
          "after": "b916000000",
          "purpose": "goroutine route length"
        },
        {
          "address": 5424022172n,
          "before": "488b8424d8010000",
          "after": "e97b000000909090",
          "purpose": "skip all remaining debug routes"
        }
      ]
    },
    "projectRefresh": {
      "address": 5411046195n,
      "originalTarget": 5411128832n,
      "refreshedTarget": 5411130208n,
      "dictionaryAddress": 5440119104n,
      "before": "e8c8420100",
      "after": "e828480100"
    },
    "oauth": {
      "code": {
        "address": 5410913216n,
        "length": 1184,
        "digest": "f9836320a98950015f66dcb3f31f6dd13ed8cd8c60a94b0c250a32ccc10379d0"
      },
      "id": {
        "address": 5431829252n,
        "length": 73,
        "digest": "bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea"
      },
      "secret": {
        "address": 5431173546n,
        "length": 35,
        "digest": "1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60"
      }
    }
  },
  {
    "version": "2.31.2",
    "originalSha256": "e40f0447109c43ece15e7025ca0864a138e0e16cb8864d4ccf64a00512a99e1a",
    "diagnosticsSha256": "b02168614d75b680cd8b0814b0d01bf91c66f5e310ff8d772516b920c8813b5f",
    "projectSha256": "33bad001301bf863bb7108e77667aaf24c649981335f5d220656c75e59818508",
    "providerSha256": "ff323094c41c50508bd4b9d8cc66230c36d2c245df68a0216010603725aa866c",
    "activityProtocol": "execution-status",
    "diagnostics": {
      "routeAddress": 5429265066n,
      "changes": [
        {
          "address": 5422819298n,
          "before": "0f84d4000000",
          "after": "e93500000090",
          "purpose": "goroutine-only route entry"
        },
        {
          "address": 5422819364n,
          "before": "488d1dd0265800",
          "after": "488d1d7f5a6200",
          "purpose": "exact goroutine route literal"
        },
        {
          "address": 5422819371n,
          "before": "b90d000000",
          "after": "b916000000",
          "purpose": "goroutine route length"
        },
        {
          "address": 5422819388n,
          "before": "488b8424d8010000",
          "after": "e97b000000909090",
          "purpose": "skip all remaining debug routes"
        }
      ]
    },
    "projectRefresh": {
      "address": 5405533491n,
      "originalTarget": 5405621728n,
      "refreshedTarget": 5405623104n,
      "dictionaryAddress": 5438566848n,
      "before": "e8a8580100",
      "after": "e8085e0100"
    },
    "oauth": {
      "code": {
        "address": 5405402464n,
        "length": 1184,
        "digest": "95fcf0f8b7ef4b2b84e5579bc1f3a722dfe8b5ac65295002aacff3134b2d4f0e"
      },
      "id": {
        "address": 5430506931n,
        "length": 73,
        "digest": "bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea"
      },
      "secret": {
        "address": 5429864528n,
        "length": 35,
        "digest": "1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60"
      }
    }
  },
  {
    "version": "2.31.0",
    "originalSha256": "77b76711786bd78b51a6879fa57e66f9f5d398ac5d369cf8b5b3e2fb5cfc741b",
    "diagnosticsSha256": "0bdb872c8092002dafdf8ef44f1b3da77c2b71ef09eaec47ea169911766b2a2b",
    "projectSha256": "39b06d761fbfa7f83090b7531b1e64c062150ba366cd97233ffc86bee3352677",
    "providerSha256": "ff0bfbb49bd798cd07d6add6f7eda235605285fa5ac25731f86d26e30ca7ff6e",
    "activityProtocol": "execution-status",
    "diagnostics": {
      "routeAddress": 5429256695n,
      "changes": [
        {
          "address": 5422808930n,
          "before": "0f84d4000000",
          "after": "e93500000090",
          "purpose": "goroutine-only route entry"
        },
        {
          "address": 5422808996n,
          "before": "488d1d302f5800",
          "after": "488d1d4c626200",
          "purpose": "exact goroutine route literal"
        },
        {
          "address": 5422809003n,
          "before": "b90d000000",
          "after": "b916000000",
          "purpose": "goroutine route length"
        },
        {
          "address": 5422809020n,
          "before": "488b8424d8010000",
          "after": "e97b000000909090",
          "purpose": "skip all remaining debug routes"
        }
      ]
    },
    "projectRefresh": {
      "address": 5405523827n,
      "originalTarget": 5405611360n,
      "refreshedTarget": 5405612736n,
      "dictionaryAddress": 5438558656n,
      "before": "e8e8550100",
      "after": "e8485b0100"
    },
    "oauth": {
      "code": {
        "address": 5405392800n,
        "length": 1184,
        "digest": "ac77aac1865b879c3a5f82520ff2fdf2e217375235d249151715f6d9cd6a147f"
      },
      "id": {
        "address": 5430498646n,
        "length": 73,
        "digest": "bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea"
      },
      "secret": {
        "address": 5429856031n,
        "length": 35,
        "digest": "1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60"
      }
    }
  },
  {
    "version": "2.29.0",
    "originalSha256": "c98e7c331da5bcc37002a550e7d625b4c636f775fa2804f73777c6a500e1c3d5",
    "diagnosticsSha256": "169355a713b8199f6fe91dd5eaf81bc7f366544b2808014425fc820ccdd04bfc",
    "projectSha256": "3269e7df7f3b34aba7b087c6ea8055163c4ace072c72ed376314775439b7453c",
    "providerSha256": "9a58d7ec73af4da1f60b9682d6632ed54a9f62dd2752372c8f53f8cbe116eb85",
    "activityProtocol": "aggregate-counts",
    "diagnostics": {
      "routeAddress": 5426190492n,
      "changes": [
        {
          "address": 5419804375n,
          "before": "0f84d4000000",
          "after": "e93500000090",
          "purpose": "goroutine-only route entry"
        },
        {
          "address": 5419804441n,
          "before": "488d1d485b5700",
          "after": "488d1d7c716100",
          "purpose": "exact goroutine route literal"
        },
        {
          "address": 5419804448n,
          "before": "b90d000000",
          "after": "b916000000",
          "purpose": "goroutine route length"
        },
        {
          "address": 5419804465n,
          "before": "488b8424d8010000",
          "after": "e97b000000909090",
          "purpose": "skip all remaining debug routes"
        }
      ]
    },
    "projectRefresh": {
      "address": 5403396979n,
      "originalTarget": 5403477312n,
      "refreshedTarget": 5403478496n,
      "dictionaryAddress": 5434567136n,
      "before": "e8c8390100",
      "after": "e8683e0100"
    },
    "oauth": {
      "code": {
        "address": 5403277248n,
        "length": 1184,
        "digest": "8f8f77b4a75c5e3e5f4a5754cd898d6ed2e27ffc10cdea864970d20637c54fc2"
      },
      "id": {
        "address": 5427400940n,
        "length": 73,
        "digest": "bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea"
      },
      "secret": {
        "address": 5426780433n,
        "length": 35,
        "digest": "1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60"
      }
    }
  },
  {
    "version": "2.28.2",
    "originalSha256": "a51a80eb1b355dc523dd9a6d7cec098ad450e3c9fb30d6001806cac86814f37b",
    "diagnosticsSha256": "684a9ea0bbba226335feac69f9fc45d27980522fe172979fd58de07f35c52441",
    "projectSha256": "d728fa9d5ce8eb69a32acbb3b820d25b1c8df901d1cee435c2c2c13963f2871b",
    "providerSha256": "c132ea0fc1688dfd614cae5ac5c47b89c6c36753375981a0600170cef896dfea",
    "activityProtocol": "aggregate-counts",
    "diagnostics": {
      "routeAddress": 5424932152n,
      "changes": [
        {
          "address": 5418628482n,
          "before": "0f84d4000000",
          "after": "e93500000090",
          "purpose": "goroutine-only route entry"
        },
        {
          "address": 5418628548n,
          "before": "488d1d48265600",
          "after": "488d1d6d2f6000",
          "purpose": "exact goroutine route literal"
        },
        {
          "address": 5418628555n,
          "before": "b90d000000",
          "after": "b916000000",
          "purpose": "goroutine route length"
        },
        {
          "address": 5418628572n,
          "before": "488b8424c0010000",
          "after": "e97b000000909090",
          "purpose": "skip all remaining debug routes"
        }
      ]
    },
    "projectRefresh": {
      "address": 5403030931n,
      "originalTarget": 5403108896n,
      "refreshedTarget": 5403109920n,
      "dictionaryAddress": 5433195904n,
      "before": "e888300100",
      "after": "e888340100"
    },
    "oauth": {
      "code": {
        "address": 5402924256n,
        "length": 1184,
        "digest": "f3a60d03f6493db9fa389fbfa726d3186f742245d54d9f94dcd8aa43560f1825"
      },
      "id": {
        "address": 5426126544n,
        "length": 73,
        "digest": "bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea"
      },
      "secret": {
        "address": 5425518723n,
        "length": 35,
        "digest": "1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60"
      }
    }
  }
];
const legacyOAuth = [
  {
    "legacyScopes": true,
    "code": {
      "address": 5400713504n,
      "length": 1184,
      "digest": "e68d7c2e7adce0f69d450f663de7c0c63dd1b85adf73e87989af530b6ae49434"
    },
    "id": {
      "address": 5422753109n,
      "length": 73,
      "digest": "bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea"
    },
    "secret": {
      "address": 5422164424n,
      "length": 35,
      "digest": "1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60"
    }
  },
  {
    "legacyScopes": true,
    "code": {
      "address": 5402072512n,
      "length": 1184,
      "digest": "3fa2eca92509332e2918fd4dbf0061fe807d808b0047971008a43f7a2ca9a6b0"
    },
    "id": {
      "address": 5424416237n,
      "length": 73,
      "digest": "bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea"
    },
    "secret": {
      "address": 5423820714n,
      "length": 35,
      "digest": "1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60"
    }
  },
  {
    "code": {
      "address": 5401936608n,
      "length": 1184,
      "digest": "d92f33d6afa4b8fa4cdcf632c6e1c2fb93cdc0264a527f0cd482227a4ded71ba"
    },
    "id": {
      "address": 5424965502n,
      "length": 73,
      "digest": "bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea"
    },
    "secret": {
      "address": 5424363131n,
      "length": 35,
      "digest": "1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60"
    }
  },
  {
    "code": {
      "address": 5402918624n,
      "length": 1184,
      "digest": "7d541382ede235246edf572dc0a193f980282fdb361ccdee674463e46c54d7c2"
    },
    "id": {
      "address": 5426122448n,
      "length": 73,
      "digest": "bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea"
    },
    "secret": {
      "address": 5425514627n,
      "length": 35,
      "digest": "1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60"
    }
  },
  {
    "code": {
      "address": 5402924256n,
      "length": 1184,
      "digest": "ddadb67ad6b8a2f4729e9ae0a74bdf276145e71e0e304c7108298f98ac595670"
    },
    "id": {
      "address": 5426126544n,
      "length": 73,
      "digest": "bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea"
    },
    "secret": {
      "address": 5425518723n,
      "length": 35,
      "digest": "1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60"
    }
  }
];
function freeze(value) { if(value && typeof value === 'object') { for(const child of Object.values(value)) freeze(child); Object.freeze(value); } return value; }
const NATIVE_PROGRAM_PROFILES = freeze(profiles);
const OAUTH_SPECIFICATIONS = freeze([...profiles.map(profile => profile.oauth), ...legacyOAuth]);
const ADAPTED_NATIVE_DIGESTS = freeze(profiles.flatMap(profile => [profile.diagnosticsSha256, profile.projectSha256, profile.providerSha256]));
module.exports = { NATIVE_PROGRAM_PROFILES, OAUTH_SPECIFICATIONS, ADAPTED_NATIVE_DIGESTS };
