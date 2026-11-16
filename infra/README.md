# Billing Service AWS

Este diretório provisiona o PostgreSQL privado do Billing Service e seu
Deployment/Service no EKS existente. O cluster, a VPC, as subnets privadas e o
security group dos workloads são obtidos da infraestrutura compartilhada.

## Variáveis no AWS Lab

No GitHub, crie o Environment `aws-lab` e configure:

- **Variables**: `AWS_REGION` e `TERRAFORM_ENVIRONMENT`.
- **Secrets**: `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` e
  `AWS_SESSION_TOKEN`.

O workflow busca automaticamente o nome do EKS e a string de conexão no SSM.
O `garage-management-database` deve ter sido aplicado antes. O role deve
confiar no OIDC do GitHub e ter permissões para EKS Describe, SSM GetParameter
com descriptografia e Kubernetes via o access entry do cluster. O bucket S3 e a chave do state são criados/definidos automaticamente pelo
`garage-management-infra`.

O workflow executa `terraform validate` em Pull Requests e `plan/apply` somente
em pushes para `master`. Senhas devem ser colocadas apenas em Secrets; nunca em
`*.tfvars` versionado.
