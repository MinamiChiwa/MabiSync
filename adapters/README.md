# 台服协议适配

`tw-20261003-gc-map.json` 是接收协议编号表；`tw-20261015-identity-schema.json` 是角色公开数据的字段结构。两者由 Core 内嵌，用于只读解析，不包含游戏二进制、真实角色记录或完整乐谱。

当前适用文件指纹：

- GameAssembly SHA-256：`1675c2131534d0ab2fc053af8a2e394d326ba7d22eff0019734be96b1808678f`
- metadata SHA-256：`3f6827a41d26ab50ef0051b881cb2c6dcab0c12aab5e95cc7da0071fae63a847`

运行观测前只核对磁盘文件，指纹不符时保留普通同步，暂停协议识别。更换客户端版本需重新验证，不能仅凭类型字段推断网络编码。

角色公开快照必须完整解析；PersonalData 只使用已经核对的持久角色编号字段。一轮停止的起点来自网络收包时间及协议偏移估计，不等于游戏实际起声时间。

解析回归使用 `tests/fixtures/` 中的合成数据：

```powershell
dotnet run --project tests/MabiSync.Observation.Tests -c Release
```
