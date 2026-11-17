# Billing Service AWS

Este diretório provisiona o PostgreSQL privado do Billing Service e seu
Deployment/Service no EKS existente. O cluster, a VPC, as subnets privadas e o
security group dos workloads são obtidos da infraestrutura compartilhada.

## Variáveis no AWS Lab

No GitHub, crie o Environment `aws-lab` e configure:

- **Variables**: `AWS_REGION`, `TERRAFORM_ENVIRONMENT` e `TF_STATE_BUCKET`.
- **Secrets**: `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` e
  `AWS_SESSION_TOKEN`.

O workflow deriva o nome do EKS como
`<project_name>-<environment>-eks` e usa `TF_STATE_BUCKET` diretamente.
A string de conexão do banco continua sendo obtida pelo Terraform no SSM.
O `garage-management-database` deve ter sido aplicado antes. O role deve
confiar no OIDC do GitHub e ter permissões para EKS Describe, SSM GetParameter
com descriptografia e Kubernetes via o access entry do cluster. O bucket S3 e a chave do state são criados/definidos automaticamente pelo
`garage-management-infra`.

O workflow executa `terraform validate` em Pull Requests e `plan/apply` somente
em pushes para `master`. O role do laboratório não precisa de `ssm:GetParameter`
para os parâmetros da plataforma. Senhas devem ser colocadas apenas em Secrets; nunca em
`*.tfvars` versionado.
