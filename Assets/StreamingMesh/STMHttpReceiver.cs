﻿// STMHttpReceiver.cs
//
//Copyright (c) 2017 Tatsuro Matsubara.
//Creative Commons License
//This file is licensed under a Creative Commons Attribution-ShareAlike 4.0 International License.
//https://creativecommons.org/licenses/by-sa/4.0/
//

using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using System.IO;

namespace StreamingMesh {

	[System.Serializable]
	public class ShaderPair : Serialize.KeyAndValue<string, Shader> {
		public ShaderPair(string key, Shader value) : base(key, value) {
		}
	}

	[System.Serializable]
	public class ShaderTable : Serialize.TableBase<string, Shader, ShaderPair> {
	}
	
	[RequireComponent(typeof(STMHttpSerializer))]
	public class STMHttpReceiver : MonoBehaviour {
        public bool referFromSerializer = true;
		public string streamFile = "stream.json";

		//serializer and material buffer, texture buffer
		STMHttpSerializer serializer;
		List<KeyValuePair<string, Material>> materialList = new List<KeyValuePair<string, Material>>();
		List<KeyValuePair<string, Texture2D>> textureList = new List<KeyValuePair<string, Texture2D>>();
		string streamInfoURL = null;

		int areaRange = 4;
		int packageSize = 128;
		float frameInterval = 0.1f;
		int subframesPerKeyframe = 4;
		int combinedFrames = 100;

		public float streamRefreshInterval = 10.0f;
		float streamCurrentWait = 0.0f;
		List<int> streamBufferList = new List<int>();
		List<int[]> streamBufferByteSize = new List<int[]>();
		List<KeyValuePair<double, byte[]>> bufferedStream = new List<KeyValuePair<double, byte[]>>();
		int currentBufferIndex;
		float vertexUpdateInterval = 0.1f;
		float currentStreamWait = 0.0f;

		public int interpolateFrames = 5;

		//temporary buffers
		List<int[]> indicesBuf = new List<int[]>();
		List<Vector3[]> vertsBuf = new List<Vector3[]>();
		List<Vector3[]> vertsBuf_old = new List<Vector3[]>();
		Vector3 position;
		Vector3 position_old;
		List<int> linedIndices = new List<int>();

		//gameobjects and meshes
		List<GameObject> meshObjects = new List<GameObject>();
		GameObject localRoot = null;
		List<Mesh> meshBuf = new List<Mesh>();

		bool isRequestComplete = false;
		int requestQueue = 0;

		float currentInterporateWait = 0.0f;
		float timeWeight = 0.0f;

		//Shaders
		public Shader defaultShader;
		public ShaderTable customShaders;

        void Reset() {
			if(localRoot != null) {
				DestroyImmediate(localRoot);
			}

			foreach(GameObject obj in meshObjects) {
				DestroyImmediate(obj);
			}

			streamBufferList.Clear();
			streamBufferByteSize.Clear();
			bufferedStream.Clear();

			meshBuf.Clear();
			materialList.Clear();
			textureList.Clear();
			streamInfoURL = null;

			indicesBuf.Clear();
			vertsBuf.Clear();
			vertsBuf_old.Clear();
			linedIndices.Clear();

			//isConnected = false;
			isRequestComplete = false;

            streamCurrentWait = streamRefreshInterval - 1.0f;

        }

		// Use this for initialization
		void Start () {
			Reset();
			InitializeReceiver();
			string url = "";
			if (referFromSerializer) {
				url = serializer.address + serializer.channel + "/" + streamFile;
			} else {
				url = streamFile;
			}
			serializer.Request(url);
		}

		void InitializeReceiver() {
			serializer = gameObject.GetComponent<STMHttpSerializer>();

			serializer.OnChannelInfoReceived = OnInitialDataReceived;
			serializer.OnMeshInfoReceived = OnMeshInfoReceived;
			serializer.OnMaterialInfoReceived = OnMaterialInfoReceived;
			serializer.OnTextureReceived = OnTextureReceived;

			serializer.OnStreamListReceived = OnStreamListReceived;
			serializer.OnStreamReceived = OnStreamDataReceived;
		}

		void OnInitialDataReceived(string name, ChannelInfo info) {
			areaRange = info.area_range;
			packageSize = info.package_size;
			frameInterval = info.frame_interval;
			combinedFrames = info.combined_frames;
			streamInfoURL = info.stream_info;

			foreach(string textureURL in info.textures) {
				serializer.Request(textureURL);
				requestQueue++;
			}

			foreach(string materialURL in info.materials) {
				serializer.Request(materialURL);
				requestQueue++;
			}

			foreach(string meshURL in info.meshes) {
				serializer.Request(meshURL);
				requestQueue++;
			}

			isRequestComplete = true;
		}

		void OnMeshInfoReceived(string name, MeshInfo info) {
			Mesh mesh = new Mesh();
			mesh.name = info.name + "_stm";

			Vector3[] verts = new Vector3[info.vertexCount];
			mesh.SetVertices(new List<Vector3>(verts));
			mesh.subMeshCount = info.subMeshCount;

			List<int> multiIndices = info.indices;
			int offset = 0;

			List<Material> materials = new List<Material>();
			for(int i = 0; i < info.subMeshCount; i++) {
				int indicesCnt = info.indicesCounts[i];
				List<int> indices = multiIndices.GetRange(offset, indicesCnt);
				offset += indicesCnt;
				mesh.SetIndices(indices.ToArray(), MeshTopology.Triangles, i);

				Material mat = null;
				foreach(KeyValuePair<string, Material> pair in materialList) {
					if (pair.Key == info.materialNames[i]) {
						mat = pair.Value;
						mat.name = pair.Key;
					}
				}
				materials.Add(mat);
			}

			mesh.uv = info.uv;
			mesh.uv2 = info.uv2;
			mesh.uv3 = info.uv3;
			mesh.uv4 = info.uv4;

			if(localRoot == null) {
				localRoot = new GameObject("ReceivedGameObject");
				localRoot.transform.SetParent(transform, false);
			}

			GameObject obj = new GameObject("Mesh" + meshBuf.Count);
			obj.transform.SetParent(localRoot.transform, false);
			MeshFilter filter = obj.AddComponent<MeshFilter>();
			MeshRenderer renderer = obj.AddComponent<MeshRenderer>();

			filter.mesh = mesh;
			renderer.materials = materials.ToArray();
			vertsBuf.Add(new Vector3[mesh.vertexCount]);
			vertsBuf_old.Add(new Vector3[mesh.vertexCount]);

			meshBuf.Add(mesh);
			meshObjects.Add(obj);
			requestQueue--;
		}

		void OnMaterialInfoReceived(string name, MaterialInfo info) {
			Material mat;
			Shader refShader = null;
			bool result = customShaders.GetTable().TryGetValue(name.TrimEnd('\0'), out refShader);
			if(result) {
				if(refShader != null) {
					mat = new Material(refShader);
				} else {
					mat = new Material(defaultShader);
				}
			} else {
				mat = new Material(defaultShader);
			}
			if (mat != null) {
				foreach(MaterialPropertyInfo tinfo in info.properties) {
					switch(tinfo.type) {
					case 0://ShaderUtil.ShaderPropertyType.Color:
						{
							Color col = JsonUtility.FromJson<Color>(tinfo.value);
							mat.SetColor(tinfo.name, col);
						}
						break;
					case 1://ShaderUtil.ShaderPropertyType.Vector:
						{
							Vector4 vec = JsonUtility.FromJson<Vector4>(tinfo.value);
							mat.SetVector(tinfo.name, vec);
						}
						break;
					case 2://ShaderUtil.ShaderPropertyType.Float:
						{
							float value = JsonUtility.FromJson<float>(tinfo.value);
							mat.SetFloat(tinfo.name, value);
						}
						break;
					case 3://ShaderUtil.ShaderPropertyType.Range:
						{
							float value = JsonUtility.FromJson<float>(tinfo.value);
							mat.SetFloat(tinfo.name, value);
						}
						break;
					case 4://ShaderUtil.ShaderPropertyType.TexEnv:
						{
							foreach(KeyValuePair<string, Texture2D> pair in textureList) {
								if (pair.Key == tinfo.value) {
									Texture2D texture = pair.Value;
									mat.SetTexture(tinfo.name, pair.Value);
								}
							}
						}
						break;
					}
				}
				//end of foreach
			}

			KeyValuePair<string, Material> matPair = new KeyValuePair<string, Material>(info.name, mat);
			materialList.Add(matPair);
			requestQueue--;
		}

		void OnTextureReceived(string name, Texture2D texture) {
			textureList.Add(new KeyValuePair<string, Texture2D>(name, texture));
			requestQueue--;
		}

		void OnStreamListReceived(string name, string list) {
			string[] lines = System.Text.RegularExpressions.Regex.Split(list, "\n");
			foreach(string line in lines) {
				StreamInfo info = JsonUtility.FromJson<StreamInfo>(line);
				if (info != null) {
					Uri uri = new Uri(info.name);
					int index;
					if (int.TryParse(Path.GetFileNameWithoutExtension(uri.AbsolutePath), out index)) {
						if (streamBufferList.Contains(index) == false) {
							streamBufferList.Add(index);
							streamBufferByteSize.Add(info.size.ToArray());
							serializer.Request(info.name);
						}
					}
				}
			}
			// End of OnStreamListReceived()
		}

		void OnStreamDataReceived(string name, byte[] data) {
			int index;
			if (int.TryParse(name, out index)) {
				if (streamBufferList.Contains(index)) {
					List<byte> rawBuffer = new List<byte>(STMHttpBaseSerializer.Decompress(data));
					List<KeyValuePair<double, byte[]>> buffers = new List<KeyValuePair<double, byte[]>>();
					int cnt = 0;
					foreach(int size in streamBufferByteSize[index]) {
						double time = index * 10 + (double)cnt * 0.1;
						byte[] buf = rawBuffer.GetRange(0, size).ToArray();
						buffers.Add(new KeyValuePair<double, byte[]>(time, buf));
						rawBuffer.RemoveRange(0, size);
						cnt++;
						//Debug.Log("INDEX: " + index + ", time: " + time + ", size: " + size);
					}
					bufferedStream.AddRange(buffers);
				}
			}
		}

		bool onlyOnce = false;

		// Update is called once per frame
		void Update() {

			if(bufferedStream.Count > 0 && !onlyOnce) {
				var player = FindObjectOfType<AudioPlayerOgg>();
				if(player != null) {
					player.StartLoading();
					Invoke("SeekToZero", 1f);
				}
                onlyOnce = true;
            }
			
			if (isRequestComplete && requestQueue < 1) {
				streamCurrentWait += Time.deltaTime;
				if (streamCurrentWait > streamRefreshInterval) {
					if (streamInfoURL != null) {
						serializer.Request(streamInfoURL);
					}
					streamCurrentWait -= streamRefreshInterval;
				}

                float delta = Time.deltaTime;

                currentStreamWait += delta;
                if (currentStreamWait > vertexUpdateInterval) {
					if (currentBufferIndex < bufferedStream.Count) {
						//Debug.Log("UpdateTime:" + bufferedStream[currentIndex].Key);
						VerticesReceived(bufferedStream[currentBufferIndex].Value);
						currentBufferIndex++;
					}
					currentStreamWait -= vertexUpdateInterval;
				}

				currentInterporateWait += delta;
                if (currentInterporateWait > frameInterval / (float)interpolateFrames) {
					currentInterporateWait -= frameInterval / (float)interpolateFrames;
					UpdateVertsInterpolate();
				}
			}

		}

		public void SeekToZero() {
			SeekTo(0);
		}

		public void SeekTo(string bufferedTime) {
			int parseTime;
			if (int.TryParse(bufferedTime, out parseTime)) {
				SeekTo(parseTime);
			}
		}

		public void SeekTo(int bufferedTime) {
			if(bufferedStream.Count == 0) {
				return;
			}
			bufferedTime = bufferedTime < bufferedStream.Count ? bufferedTime : bufferedStream.Count - 1;
			int segment = bufferedTime / (subframesPerKeyframe + 1);
			int iTime = segment * (subframesPerKeyframe + 1);
			currentBufferIndex = iTime;
			byte[] data = bufferedStream[currentBufferIndex].Value;
			VerticesReceived(data);
			VerticesReceived(data);
			currentBufferIndex++;
			currentStreamWait = 0;
			currentInterporateWait = 0;
			UpdateVertsInterpolate();
		}

		void UpdateVertsInterpolate() {
			if(timeWeight < 1.0f) {
                for (int i = 0; i < vertsBuf.Count; i++) {
                    Vector3[] buf = vertsBuf[i];
                    Vector3[] tempBuf = vertsBuf_old[i].Clone() as Vector3[];
                    
					for(int j = 0; j < tempBuf.Length; j++) {
                        float cx = tempBuf[j].x;
                        float cy = tempBuf[j].y;
                        float cz = tempBuf[j].z;

                        float rx = (buf[j].x - cx) * timeWeight;
                        float ry = (buf[j].y - cy) * timeWeight;
                        float rz = (buf[j].z - cz) * timeWeight;
                        tempBuf[j].Set(cx + rx, cy + ry, cz + rz);

                        //Vector3 range = buf[j] - tempBuf[j];
                        //tempBuf[j] += range * timeWeight;
                    }

                    meshBuf[i].vertices = tempBuf;
                    if (updateNormals == true) {
                        meshBuf[i].RecalculateNormals();
                        meshBuf[i].RecalculateBounds();
                        updateNormals = false;
                    }
                    
                }
            }
            float c_pX = position_old.x;
            float c_pY = position_old.y;
            float c_pZ = position_old.z;
            float r_pX = (position.x - c_pX) * timeWeight;
            float r_pY = (position.y - c_pY) * timeWeight;
            float r_pZ = (position.z - c_pZ) * timeWeight;
            localRoot.transform.localPosition.Set(c_pX + r_pX, c_pY + r_pY, c_pZ + r_pZ);

            timeWeight += 1.0f / interpolateFrames;
		}

		bool getErrorData = false;
        bool updateNormals = false;

		public void VerticesReceived(byte[] data)
		{
			if(isRequestComplete) {
                for(int i = 0; i < vertsBuf.Count; i++) {
                    vertsBuf[i].CopyTo(vertsBuf_old[i], 0);
                }
                position_old = position;

				int packages = (data[7] << 16) + (data[6] << 8) + data[5];
                //bool isCompressed = data[8] == 0x01 ? true : false;

                position.x = BitConverter.ToSingle(data, 9);
                position.y = BitConverter.ToSingle(data, 13);
                position.z = BitConverter.ToSingle(data, 17);

				int offset = 21;

				byte[] buf = data;

				if(data[0] == 0x0F) {
                    updateNormals = true;
                    linedIndices.Clear();

                    int hk = packageSize / 2;
                    float qk = areaRange / (float)hk;
                    float sqk = qk / 32f;

                    for (int i = 0; i < packages; i++) {
                        float t_x = (buf[offset]     - hk) * qk;
                        float t_y = (buf[offset + 1] - hk) * qk;
                        float t_z = (buf[offset + 2] - hk) * qk;

                        int vertCount = (buf[offset + 5] << 16) + (buf[offset + 4] << 8) + buf[offset + 3];
                        offset += 6;
						for(int j = 0; j < vertCount; j++) {
                            int lIdx = offset + j * 5;
                            int vIdx = (buf[lIdx + 1] << 8) + buf[lIdx];
                            int mIdx = buf[lIdx + 2];
                            if(mIdx >= vertsBuf.Count) {
                                getErrorData = true;
                                continue;
                            }
                            if(vIdx >= vertsBuf[mIdx].Length) {
                                getErrorData = true;
                                continue;
                            }

                            int compress = (buf[lIdx + 4] << 8) + buf[lIdx + 3];

							float v_x = compress & 0x1F;
							float v_y = (compress >> 5) & 0x1F;
							float v_z = (compress >> 10) & 0x1F;

                            float x = t_x + v_x * sqk;
							float y = t_y + v_y * sqk;
							float z = t_z + v_z * sqk;

                            vertsBuf[mIdx][vIdx].Set(x, y, z);

							linedIndices.Add((mIdx << 16) + vIdx);
							getErrorData = false;
						}
                        if(getErrorData) {
                            Debug.LogError("data broken in VerticesReceived()");
                        }

                        offset += (vertCount * 5);
					}
				} else if(data[0] == 0x0E && !getErrorData) {
                    int i = 0;
                    const float d = 0.0078125f; // 1 / 128f;
                    linedIndices.ForEach((idx) => {
                        int lIdx = offset + i * 3;
                        int mIdx = (idx >> 16) & 0xFF;
                        int vIdx = idx & 0xFFFF;

                        float dx = (buf[lIdx] - 128) * d;
                        float dy = (buf[lIdx + 1] - 128) * d;
                        float dz = (buf[lIdx + 2] - 128) * d;
                        float x = Mathf.Sign(dx) * (dx * dx);
                        float y = Mathf.Sign(dy) * (dy * dy);
                        float z = Mathf.Sign(dz) * (dz * dz);
                        Vector3[] vec = vertsBuf[mIdx];
                        vec[vIdx].Set(vec[vIdx].x + x, vec[vIdx].y + y, vec[vIdx].z + z);
                        ++i;
                    });
				}
			}
			timeWeight = 0.0f;
		}
	}
}
